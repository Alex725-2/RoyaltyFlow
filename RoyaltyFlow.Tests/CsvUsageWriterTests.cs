using System.Globalization;
using RoyaltyFlow.CsvGenerator;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class CsvUsageWriterTests
{
    [Fact]
    public void SameSeedProducesIdenticalCsvAcrossCultures()
    {
        var first = Generate(100, 42, "en-US");
        Assert.Equal(first, Generate(100, 42, "ar-SA"));
        Assert.NotEqual(first, Generate(100, 43, "en-US"));
        var lines = first.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(101, lines.Length);
        Assert.Equal("EventId,TrackId,Platform,Country,UsageDate,PlayCount", lines[0]);

        var eventIds = lines.Skip(1).Select(line => Guid.Parse(line.Split(',')[0]));
        Assert.Equal(100, eventIds.Distinct().Count());
    }

    [Fact]
    public void IncreasingCountPreservesExistingEvents()
    {
        Assert.StartsWith(Generate(10, -42, "en-US"), Generate(100, -42, "en-US"));
    }

    private static string Generate(int count, int seed, string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var writer = new StringWriter();
            CsvUsageWriter.Write(writer, count, seed);
            return writer.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
