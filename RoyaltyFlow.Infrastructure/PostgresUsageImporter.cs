using Dapper;
using Npgsql;
using NpgsqlTypes;
using RoyaltyFlow.Application;
using RoyaltyFlow.Domain;

namespace RoyaltyFlow.Infrastructure;

public sealed class PostgresUsageImporter(NpgsqlDataSource source, RoyaltyCalculator calculator) : IUsageImporter
{
    private const string CreateStage = """
        CREATE TEMP TABLE usage_stage (
            ordinal bigint NOT NULL, event_id uuid NOT NULL, track_id uuid NOT NULL,
            platform varchar(64) NOT NULL, country varchar(2) NOT NULL, usage_date date NOT NULL,
            play_count bigint NOT NULL
        ) ON COMMIT DROP
        """;

    private const string Eligible = """
        SELECT s.* FROM usage_stage s JOIN tracks t ON t.id = s.track_id
        """;

    public async Task<ImportPreview> PreviewAsync(Stream csv, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stage = await StageAsync(connection, transaction, csv, cancellationToken);
        var counts = await connection.QuerySingleAsync<Counts>(new CommandDefinition($"""
            WITH eligible AS ({Eligible})
            SELECT count(*) AS "Eligible",
                count(DISTINCT e.event_id) FILTER (WHERE u.event_id IS NULL) AS "Inserted"
            FROM eligible e LEFT JOIN usage_records u ON u.event_id = e.event_id
            """, transaction: transaction, cancellationToken: cancellationToken));
        await transaction.RollbackAsync(cancellationToken);
        return new(ImportStatistics.Classify(stage.Total, counts.Eligible, counts.Inserted), stage.Issues);
    }

    public async Task<ImportOutcome> UploadAsync(Stream csv, string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO usage_imports (id, sha256, status) VALUES (@Id, @Sha256, 'Pending')
            ON CONFLICT (sha256) DO NOTHING
            """, new { Id = Guid.NewGuid(), Sha256 = sha256 }, cancellationToken: cancellationToken));

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var import = await ReadImportAsync(connection, transaction, sha256, cancellationToken);
        if (import.Status == ImportStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(import, true);
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE usage_imports SET status = 'Processing', started_at = clock_timestamp(),
                finished_at = NULL, duration_ms = NULL WHERE id = @Id
            """, new { import.Id }, transaction, cancellationToken: cancellationToken));
        await transaction.SaveAsync("import_work", cancellationToken);

        ImportReceipt receipt;
        try
        {
            var stage = await StageAsync(connection, transaction, csv, cancellationToken);
            var counts = await connection.QuerySingleAsync<Counts>(new CommandDefinition($"""
                WITH eligible AS MATERIALIZED ({Eligible}),
                inserted AS (
                    INSERT INTO usage_records (event_id, track_id, import_id, platform, country, usage_date, play_count)
                    SELECT DISTINCT ON (event_id) event_id, track_id, @Id, platform, country, usage_date, play_count
                    FROM eligible ORDER BY event_id, ordinal
                    ON CONFLICT (event_id) DO NOTHING RETURNING event_id
                )
                SELECT (SELECT count(*) FROM eligible) AS "Eligible",
                       (SELECT count(*) FROM inserted) AS "Inserted"
                """, new { import.Id }, transaction, cancellationToken: cancellationToken));

            if (counts.Inserted > 0)
                await AccrueAsync(connection, transaction, import.Id, cancellationToken);

            var statistics = ImportStatistics.Classify(stage.Total, counts.Eligible, counts.Inserted);
            receipt = await FinishAsync(connection, transaction, import.Id, ImportStatus.Completed,
                statistics, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The outer transaction retains the hash row lock while the failed work is rolled back.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await transaction.RollbackAsync("import_work", cleanup.Token);
            await FinishAsync(connection, transaction, import.Id, ImportStatus.Failed,
                new ImportStatistics(0, 0, 0, 0), cleanup.Token);
            await transaction.CommitAsync(cleanup.Token);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        return new(receipt, false);
    }

    private static async Task<StageResult> StageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Stream csv, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(CreateStage, transaction: transaction,
            cancellationToken: cancellationToken));
        await using var copy = await connection.BeginBinaryImportAsync(
            "COPY usage_stage (ordinal,event_id,track_id,platform,country,usage_date,play_count) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        long total = 0;
        var issues = new List<CsvIssue>();
        await foreach (var row in UsageCsvReader.ReadAsync(csv, cancellationToken))
        {
            total++;
            if (row.Record is not { } record)
            {
                if (issues.Count < 20)
                    issues.Add(new(row.Line, row.Error!));
                continue;
            }

            await copy.StartRowAsync(cancellationToken);
            await copy.WriteAsync(row.Line, NpgsqlDbType.Bigint, cancellationToken);
            await copy.WriteAsync(record.EventId, NpgsqlDbType.Uuid, cancellationToken);
            await copy.WriteAsync(record.TrackId, NpgsqlDbType.Uuid, cancellationToken);
            await copy.WriteAsync(record.Platform, NpgsqlDbType.Varchar, cancellationToken);
            await copy.WriteAsync(record.Country, NpgsqlDbType.Varchar, cancellationToken);
            await copy.WriteAsync(record.UsageDate, NpgsqlDbType.Date, cancellationToken);
            await copy.WriteAsync(record.PlayCount, NpgsqlDbType.Bigint, cancellationToken);
        }
        await copy.CompleteAsync(cancellationToken);
        await copy.DisposeAsync();
        await connection.ExecuteAsync(new CommandDefinition("ANALYZE usage_stage", transaction: transaction,
            cancellationToken: cancellationToken));
        return new(total, issues);
    }

    private async Task AccrueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TEMP TABLE accrual_stage ON COMMIT DROP AS
            SELECT u.event_id, c.id AS contract_id FROM usage_records u
            JOIN royalty_contracts c ON c.track_id = u.track_id
                AND daterange(c.valid_from, c.valid_to, '[)') @> u.usage_date
            WHERE u.import_id = @ImportId;
            CREATE UNIQUE INDEX ON accrual_stage (event_id);
            ANALYZE accrual_stage;
            """, new { ImportId = importId }, transaction, cancellationToken: cancellationToken));

        Guid? after = null;
        while (true)
        {
            var batch = (await connection.QueryAsync<AccrualInput>(new CommandDefinition("""
                WITH batch AS (
                    SELECT s.event_id, s.contract_id FROM accrual_stage s
                    WHERE s.event_id > coalesce(CAST(@After AS uuid), '00000000-0000-0000-0000-000000000000'::uuid)
                    ORDER BY s.event_id LIMIT 500
                )
                SELECT b.event_id AS "EventId", u.track_id AS "TrackId", u.usage_date::timestamp AS "UsageDate",
                    u.play_count AS "PlayCount", b.contract_id AS "ContractId", s.rights_holder_id AS "RightsHolderId",
                    s.percentage AS "Percentage"
                FROM batch b JOIN usage_records u ON u.event_id = b.event_id
                LEFT JOIN royalty_shares s ON s.contract_id = b.contract_id
                ORDER BY b.event_id
                """, new { After = after }, transaction, cancellationToken: cancellationToken))).AsList();
            if (batch.Count == 0)
                return;

            after = batch[^1].EventId;
            await using var copy = await connection.BeginBinaryImportAsync(
                "COPY royalty_accruals (event_id,contract_id,rights_holder_id,amount) FROM STDIN (FORMAT BINARY)", cancellationToken);
            foreach (var group in batch.GroupBy(row => row.EventId))
            {
                var row = group.First();
                var contract = new RoyaltyContract(row.ContractId, row.TrackId,
                    group.Where(share => share.RightsHolderId.HasValue)
                        .Select(share => new RoyaltyShare(share.RightsHolderId!.Value, share.Percentage!.Value)));
                var usage = new UsageRecord(row.EventId, row.TrackId, "", "", DateOnly.FromDateTime(row.UsageDate), row.PlayCount);
                foreach (var accrual in calculator.Calculate(usage, contract))
                {
                    await copy.StartRowAsync(cancellationToken);
                    await copy.WriteAsync(accrual.EventId, NpgsqlDbType.Uuid, cancellationToken);
                    await copy.WriteAsync(accrual.ContractId, NpgsqlDbType.Uuid, cancellationToken);
                    await copy.WriteAsync(accrual.RightsHolderId, NpgsqlDbType.Uuid, cancellationToken);
                    await copy.WriteAsync(accrual.Amount, NpgsqlDbType.Numeric, cancellationToken);
                }
            }
            await copy.CompleteAsync(cancellationToken);
        }
    }

    private const string ImportColumns = """
        id AS "Id", sha256 AS "Sha256", status AS "Status", total AS "Total", inserted AS "Inserted",
        duplicate AS "Duplicate", invalid AS "Invalid", created_at AS "CreatedAt", started_at AS "StartedAt",
        finished_at AS "FinishedAt", duration_ms AS "DurationMs"
        """;

    private static async Task<ImportReceipt> ReadImportAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string sha256, CancellationToken cancellationToken) =>
        (await connection.QuerySingleAsync<ImportRow>(new CommandDefinition(
            $"SELECT {ImportColumns} FROM usage_imports WHERE sha256 = @Sha256 FOR UPDATE",
            new { Sha256 = sha256 }, transaction, cancellationToken: cancellationToken))).ToReceipt();

    private static async Task<ImportReceipt> FinishAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, ImportStatus status, ImportStatistics statistics, CancellationToken cancellationToken) =>
        (await connection.QuerySingleAsync<ImportRow>(new CommandDefinition($"""
            WITH timing AS (SELECT clock_timestamp() AS finished)
            UPDATE usage_imports SET status = @Status, total = @Total, inserted = @Inserted,
                duplicate = @Duplicate, invalid = @Invalid, finished_at = timing.finished,
                duration_ms = greatest(0, floor(extract(epoch FROM timing.finished - started_at) * 1000)::bigint)
            FROM timing WHERE id = @Id RETURNING {ImportColumns}
            """, new { Id = id, Status = status.ToString(), statistics.Total, statistics.Inserted,
                statistics.Duplicate, statistics.Invalid }, transaction, cancellationToken: cancellationToken))).ToReceipt();

    private sealed record StageResult(long Total, List<CsvIssue> Issues);
    private sealed class Counts
    {
        public long Eligible { get; init; }
        public long Inserted { get; init; }
    }
    private sealed class AccrualInput
    {
        public Guid EventId { get; init; }
        public Guid TrackId { get; init; }
        public DateTime UsageDate { get; init; }
        public long PlayCount { get; init; }
        public Guid ContractId { get; init; }
        public Guid? RightsHolderId { get; init; }
        public decimal? Percentage { get; init; }
    }
}
