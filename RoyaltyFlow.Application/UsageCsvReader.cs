using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using RoyaltyFlow.Domain;

namespace RoyaltyFlow.Application;

public sealed record CsvRow(long Line, UsageRecord? Record, string? Error);

public static class UsageCsvReader
{
    public const string Header = "EventId,TrackId,Platform,Country,UsageDate,PlayCount";
    public const int MaxLineLength = 1024;
    public const int MaxPlayCount = 1_000_000_000;

    public static async IAsyncEnumerable<CsvRow> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[8192];
        var line = new StringBuilder(MaxLineLength);
        long lineNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            try { read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken); }
            catch (DecoderFallbackException) { throw new CsvFormatException("The CSV must be valid UTF-8."); }

            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = ParseLine(++lineNumber, line.ToString());
                    line.Clear();
                    if (row is not null)
                        yield return row;
                }
                else
                {
                    if (line.Length == MaxLineLength)
                        throw new CsvFormatException($"Line {lineNumber + 1} exceeds {MaxLineLength} characters.");
                    line.Append(character);
                }
            }
        }

        if (line.Length > 0 && ParseLine(++lineNumber, line.ToString()) is { } last)
            yield return last;
        if (lineNumber == 0)
            throw new CsvFormatException("The CSV header is missing.");
    }

    private static CsvRow? ParseLine(long lineNumber, string line)
    {
        if (line.EndsWith('\r'))
            line = line[..^1];
        if (lineNumber == 1)
        {
            if (line.TrimStart('\uFEFF') != Header)
                throw new CsvFormatException($"Expected CSV header: {Header}.");
            return null;
        }

        // The interchange format permits one physical line per record; quoted fields may contain escaped quotes.
        if (line.Contains('\r') || !TrySplit(line, out var fields))
            throw new CsvFormatException($"Invalid CSV structure at line {lineNumber}.");

        if (fields.Count != 6 ||
            !Guid.TryParseExact(fields[0], "D", out var eventId) || eventId == Guid.Empty ||
            !Guid.TryParseExact(fields[1], "D", out var trackId) || trackId == Guid.Empty ||
            string.IsNullOrWhiteSpace(fields[2]) || fields[2].Length > 64 || fields[2].Any(char.IsControl) ||
            fields[3].Length != 2 || fields[3].Any(letter => letter is < 'A' or > 'Z') ||
            !DateOnly.TryParseExact(fields[4], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ||
            !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var plays) ||
            plays < 0 || plays > MaxPlayCount)
            return new(lineNumber, null, "Expected a nonempty event and track UUID, platform, country, ISO date and valid play count.");

        return new(lineNumber, new UsageRecord(eventId, trackId, fields[2], fields[3], date, plays), null);
    }

    private static bool TrySplit(string line, out List<string> fields)
    {
        fields = [];
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character != '"')
                    field.Append(character);
                else if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                    closed = true;
                }
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                closed = false;
            }
            else if (closed)
                return false;
            else if (character == '"')
            {
                if (field.Length != 0)
                    return false;
                quoted = true;
            }
            else
                field.Append(character);
        }

        fields.Add(field.ToString());
        return !quoted;
    }
}
