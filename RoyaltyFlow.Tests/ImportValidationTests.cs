using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using RoyaltyFlow.Api;
using RoyaltyFlow.Application;
using RoyaltyFlow.Infrastructure;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class ImportValidationTests
{
    private const string Id = "10000000-0000-0000-0000-000000000001";
    private const string Track = "20000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("0", "2024-02-29", true)]
    [InlineData("1000000000", "2024-02-29", true)]
    [InlineData("1000000001", "2024-02-29", false)]
    [InlineData("-1", "2024-02-29", false)]
    [InlineData("2147483648", "2024-02-29", false)]
    [InlineData("1", "2025-02-29", false)]
    [InlineData("1", "2025/01/01", false)]
    public async Task ValidatesRows(string plays, string date, bool valid)
    {
        var rows = await ParseAsync($"{UsageCsvReader.Header}\r\n{Id},{Track},\"Demo, Music\",US,{date},{plays}");
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Line);
        Assert.Equal(valid, row.Record is not null);
        if (valid)
            Assert.Equal("Demo, Music", row.Record!.Platform);
    }

    [Fact]
    public async Task EmptyUuidIsAnInvalidRow()
    {
        var rows = await ParseAsync($"{UsageCsvReader.Header}\n{Guid.Empty:D},{Track},Demo,US,2025-01-01,1");
        Assert.Null(Assert.Single(rows).Record);
    }

    [Fact]
    public async Task StructuralErrorsRejectTheFile()
    {
        await Assert.ThrowsAsync<CsvFormatException>(() => ParseAsync("invalid"));
        await Assert.ThrowsAsync<CsvFormatException>(() => ParseAsync($"{UsageCsvReader.Header}\n\"broken"));
        await Assert.ThrowsAsync<CsvFormatException>(() =>
            ParseAsync($"{UsageCsvReader.Header}\n{new string('a', UsageCsvReader.MaxLineLength + 1)}"));
        using var invalidUtf8 = new MemoryStream([0xff, 0xff, 0xff]);
        await Assert.ThrowsAsync<CsvFormatException>(async () =>
        {
            await foreach (var _ in UsageCsvReader.ReadAsync(invalidUtf8)) { }
        });
    }

    [Fact]
    public async Task DuplicateRowsKeepOrdinalsForDatabaseDeduplication()
    {
        var line = $"{Id},{Track},Demo,US,2025-01-01,1";
        var rows = await ParseAsync($"{UsageCsvReader.Header}\n{line}\n{line}\n");
        Assert.Equal(rows[0].Record!.EventId, rows[1].Record!.EventId);
        Assert.Equal(new long[] { 2, 3 }, rows.Select(row => row.Line));
        Assert.Equal(new ImportStatistics(3, 1, 1, 1), ImportStatistics.Classify(3, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImportStatistics.Classify(1, 2, 1));
    }

    [Fact]
    public async Task ParserHonorsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(UsageCsvReader.Header));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in UsageCsvReader.ReadAsync(stream, cancel.Token)) { }
        });
    }

    [Fact]
    public async Task UploadHashesOriginalBytesAndEnforcesLimit()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "spool-tests");
        using var input = new MemoryStream([1, 2, 3]);
        await using (var upload = await CsvUpload.ReadAsync(input, directory, default, 3))
        {
            Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), upload.Sha256);
            Assert.Equal(0, upload.Stream.Position);
        }
        input.Position = 0;
        await Assert.ThrowsAsync<Microsoft.AspNetCore.Http.BadHttpRequestException>(async () =>
        {
            await using var _ = await CsvUpload.ReadAsync(input, directory, default, 2);
        });
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public void ContractDtoReusesDomainInvariants()
    {
        var holder = Guid.Parse(Id);
        var valid = new CreateContractRequest(Guid.Parse(Track), new(2025, 1, 1), null, [new(holder, 100)]);
        Assert.Single(valid.ToDomain().Shares);
        Assert.Throws<ArgumentException>(() => (valid with { ValidTo = valid.ValidFrom }).ToDomain());
        Assert.Throws<ArgumentException>(() => (valid with { Shares = [new(holder, 99)] }).ToDomain());
    }

    [Fact]
    public void EfMigrationCanGenerateAnIdempotentScript()
    {
        using var db = new RoyaltyDbContext(new DbContextOptionsBuilder<RoyaltyDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);
        Assert.Single(db.Database.GetMigrations());
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE usage_records", script);
        Assert.Contains("EXCLUDE USING gist", script);
        Assert.Equal("usage_records", db.Model.FindEntityType(typeof(RoyaltyFlow.Domain.UsageRecord))!.GetTableName());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SwaggerDocumentsCsvAndAllApiRoutes()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var schema = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = schema.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/artists", "/api/tracks", "/api/rights-holders", "/api/contracts",
                     "/api/imports", "/api/imports/{id}", "/api/usage", "/api/royalties/summary", "/health/live", "/health/ready" })
            Assert.True(paths.GetProperty(path).TryGetProperty("get", out _), path);
        foreach (var path in new[] { "/api/artists", "/api/tracks", "/api/rights-holders", "/api/contracts" })
            Assert.True(paths.GetProperty(path).TryGetProperty("post", out _), path);
        foreach (var path in new[] { "/api/imports/preview", "/api/imports/upload" })
            Assert.Equal("binary", paths.GetProperty(path).GetProperty("post").GetProperty("requestBody")
                .GetProperty("content").GetProperty("text/csv").GetProperty("schema").GetProperty("format").GetString());
    }

    [Fact]
    public async Task MissingDatabaseKeepsLivenessAvailable()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => TestAccess.Configure(configuration, "")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = TestAccess.Demo;
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var catalog = await client.GetAsync("/api/artists");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, catalog.StatusCode);
        Assert.Equal("application/problem+json", catalog.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task InvalidRequestsReturnProblemDetailsBeforeDatabaseAccess()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                TestAccess.Configure(configuration, "Host=localhost;Database=unused")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = TestAccess.Operator;
        using var invalidPage = await client.GetAsync("/api/usage?pageSize=201");
        using var invalidContract = await client.PostAsJsonAsync("/api/contracts", new CreateContractRequest(
            Guid.NewGuid(), new(2025, 1, 1), new(2024, 1, 1), [new(Guid.NewGuid(), 100)]));
        using var invalidFormat = await client.PostAsync("/api/imports/upload",
            new StringContent("test", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidContract.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalidFormat.StatusCode);
        Assert.Equal("application/problem+json", invalidContract.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<List<CsvRow>> ParseAsync(string csv)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var rows = new List<CsvRow>();
        await foreach (var row in UsageCsvReader.ReadAsync(stream))
            rows.Add(row);
        return rows;
    }
}
