using Dapper;
using Npgsql;
using RoyaltyFlow.Application;

namespace RoyaltyFlow.Infrastructure;

public sealed class PostgresRoyaltyAnalytics(NpgsqlDataSource source) : IRoyaltyAnalytics
{
    public async Task<IReadOnlyList<UsageSummary>> SummarizeAsync(DateOnly from, DateOnly to,
        int offset, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<UsageSummary>(new CommandDefinition("""
            WITH period AS MATERIALIZED (
                SELECT event_id, track_id, play_count FROM usage_records
                WHERE usage_date BETWEEN CAST(@From AS date) AND CAST(@To AS date)
            ),
            usage_by_track AS (
                SELECT track_id, count(*) AS events, sum(play_count) AS plays
                FROM period GROUP BY track_id ORDER BY track_id LIMIT @Limit OFFSET @Offset
            ),
            accruals_by_track AS (
                SELECT p.track_id, sum(a.amount) AS amount
                FROM usage_by_track b JOIN period p ON p.track_id = b.track_id
                JOIN royalty_accruals a ON a.event_id = p.event_id GROUP BY p.track_id
            )
            SELECT b.track_id AS "TrackId", t.title AS "Title", b.events AS "Events",
                b.plays AS "PlayCount", coalesce(a.amount, 0::numeric) AS "RoyaltyAmount"
            FROM usage_by_track b JOIN tracks t ON t.id = b.track_id
            LEFT JOIN accruals_by_track a ON a.track_id = b.track_id ORDER BY b.track_id
            """, new { From = from.ToDateTime(TimeOnly.MinValue), To = to.ToDateTime(TimeOnly.MinValue), Offset = offset, Limit = limit },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
