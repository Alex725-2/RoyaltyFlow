using System.Globalization;
using System.Text;
using RoyaltyFlow.CsvGenerator;

const string usage = "Usage: --count <positive integer> --output <path> --seed <integer>";
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(usage);
    return 0;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index + 1 < args.Length; index += 2)
    options.TryAdd(args[index], args[index + 1]);

if (args.Length != 6 || options.Count != 3 ||
    !options.TryGetValue("--count", out var countText) ||
    !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
    count <= 0 ||
    !options.TryGetValue("--seed", out var seedText) ||
    !int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) ||
    !options.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output))
{
    Console.Error.WriteLine(usage);
    return 2;
}

try
{
    var path = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
    CsvUsageWriter.Write(writer, count, seed);
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
