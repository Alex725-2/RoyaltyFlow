using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoyaltyFlow.Application;
using RoyaltyFlow.Infrastructure;

namespace RoyaltyFlow.Api;

[ApiController]
[Route("api/imports")]
public sealed class ImportsController(RoyaltyDbContext db, IUsageImporter importer,
    IWebHostEnvironment environment, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<PageResult<ImportReceipt>> Imports([FromQuery(Name = "")] PageQuery page, CancellationToken token)
    {
        var rows = await db.Imports.AsNoTracking().OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id).Skip(page.Offset).Take(page.PageSize + 1).ToListAsync(token);
        return PageResult<ImportReceipt>.From(rows.Select(row => row.ToReceipt()).ToArray(), page);
    }

    [HttpGet("{id:guid}", Name = "GetImport")]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<ImportReceipt>> Import(Guid id, CancellationToken token)
    {
        var row = await db.Imports.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, token);
        if (row is null)
            return Problem(statusCode: 404, title: "Import not found.");
        return row.ToReceipt();
    }

    [HttpPost("preview")]
    [Consumes("text/csv"), CsvBody, RequestSizeLimit(CsvUpload.MaxBytes)]
    public async Task<ActionResult<ImportPreview>> Preview(CancellationToken token)
    {
        await using var csv = await OpenCsvAsync(token);
        return await importer.PreviewAsync(csv.Stream, token);
    }

    [HttpPost("upload")]
    [Consumes("text/csv"), CsvBody, RequestSizeLimit(CsvUpload.MaxBytes)]
    [ProducesResponseType<ImportReceipt>(201)]
    [ProducesResponseType<ImportReceipt>(200)]
    public async Task<ActionResult<ImportReceipt>> Upload(CancellationToken token)
    {
        await using var csv = await OpenCsvAsync(token);
        var result = await importer.UploadAsync(csv.Stream, csv.Sha256, token);
        if (result.Reused)
            return Ok(result.Import);
        return CreatedAtRoute("GetImport", new { id = result.Import.Id }, result.Import);
    }

    private Task<CsvUpload> OpenCsvAsync(CancellationToken token)
    {
        if (Request.ContentLength > CsvUpload.MaxBytes)
            throw new BadHttpRequestException("CSV exceeds the upload size limit.", 413);
        if (!string.Equals(Request.ContentType?.Split(';', 2)[0].Trim(), "text/csv", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Send the CSV as a text/csv request body.", 415);

        var directory = configuration["Imports:SpoolDirectory"] ?? Path.Combine("obj", "uploads");
        return CsvUpload.ReadAsync(Request.Body, Path.GetFullPath(directory, environment.ContentRootPath), token);
    }
}
