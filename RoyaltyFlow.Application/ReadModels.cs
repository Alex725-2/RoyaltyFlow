namespace RoyaltyFlow.Application;

public sealed record UsageSummary(Guid TrackId, string Title, long Events, decimal PlayCount, decimal RoyaltyAmount);

public interface IRoyaltyAnalytics
{
    Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly from, DateOnly to,
        int offset, int limit, CancellationToken cancellationToken);
}
