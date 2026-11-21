using System.Buffers.Binary;
using System.Globalization;

namespace RoyaltyFlow.CsvGenerator;

public static class CsvUsageWriter
{
    private static readonly string[] Platforms = ["DemoStream", "DemoMusic", "DemoRadio"];
    private static readonly string[] Countries = ["BR", "DE", "JP", "US"];
    private static readonly DateOnly StartDate = new(2025, 1, 1);

    public static void Write(TextWriter writer, int count, int seed)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var random = new Random(seed);

        writer.Write("EventId,TrackId,Platform,Country,UsageDate,PlayCount\n");

        for (var row = 0; row < count; row++)
        {
            var eventId = CreateId(seed, row, 1);
            var trackId = CreateId(0, random.Next(1, 1001), 2);
            var platform = Platforms[random.Next(Platforms.Length)];
            var country = Countries[random.Next(Countries.Length)];
            var usageDate = StartDate.AddDays(random.Next(365));
            var playCount = random.Next(1, 1001);

            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{eventId:D},{trackId:D},{platform},{country},{usageDate:yyyy-MM-dd},{playCount}\n"));
        }
    }

    private static Guid CreateId(int seed, int index, byte kind)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, seed);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], index);
        bytes[8] = kind;
        return new Guid(bytes);
    }
}
