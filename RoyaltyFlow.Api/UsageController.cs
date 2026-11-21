using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RoyaltyFlow.Application;
using RoyaltyFlow.Domain;
using RoyaltyFlow.Infrastructure;

namespace RoyaltyFlow.Api;

[ApiController]
[Route("api")]
public sealed class UsageController(RoyaltyDbContext db, IRoyaltyAnalytics analytics) : ControllerBase
{
    [HttpGet("usage")]
    public async Task<ActionResult<PageResult<UsageRecord>>> Usage([FromQuery] PageQuery page, Guid? trackId,
        DateOnly? from, DateOnly? to, CancellationToken token)
    {
        if (trackId == Guid.Empty || from > to)
            return Problem(statusCode: 400, title: "Invalid track or date range.");
        IQueryable<UsageRecord> query = db.Usage;
        if (trackId.HasValue)
            query = query.Where(row => row.TrackId == trackId.Value);
        if (from.HasValue)
            query = query.Where(row => row.UsageDate >= from.Value);
        if (to.HasValue)
            query = query.Where(row => row.UsageDate <= to.Value);
        return await CatalogController.FetchAsync(
            query.OrderByDescending(row => row.UsageDate).ThenByDescending(row => row.EventId), page, token);
    }

    [HttpGet("royalties/summary")]
    public async Task<ActionResult<PageResult<UsageSummary>>> Summary([FromQuery] PageQuery page, DateOnly? from,
        DateOnly? to, CancellationToken token)
    {
        if (!from.HasValue || !to.HasValue || from > to)
            return Problem(statusCode: 400, title: "Provide a valid inclusive date range.");
        return PageResult<UsageSummary>.From(
            await analytics.SummarizeAsync(from.Value, to.Value, page.Offset, page.PageSize + 1, token), page);
    }
}
