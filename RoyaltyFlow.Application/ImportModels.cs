namespace RoyaltyFlow.Application;

public enum ImportStatus { Pending, Processing, Completed, Failed }

public sealed record ImportStatistics(long Total, long Inserted, long Duplicate, long Invalid)
{
    public static ImportStatistics Classify(long total, long eligible, long inserted)
    {
        if (inserted < 0 || eligible < inserted || total < eligible)
            throw new ArgumentOutOfRangeException(nameof(inserted), "Expected 0 <= inserted <= eligible <= total.");

        return new(total, inserted, eligible - inserted, total - eligible);
    }
}

public sealed record ImportPreview(ImportStatistics Statistics, IReadOnlyList<CsvIssue> Issues);
public sealed record CsvIssue(long Line, string Message);
public sealed record ImportReceipt(Guid Id, string Sha256, ImportStatus Status, ImportStatistics Statistics,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, long? DurationMs);
public sealed record ImportOutcome(ImportReceipt Import, bool Reused);

public interface IUsageImporter
{
    Task<ImportPreview> PreviewAsync(Stream csv, CancellationToken cancellationToken);
    Task<ImportOutcome> UploadAsync(Stream csv, string sha256, CancellationToken cancellationToken);
}

public sealed class CsvFormatException(string message) : Exception(message);
