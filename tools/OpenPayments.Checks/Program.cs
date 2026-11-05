using System.Globalization;
using System.Text.Json;
using OpenPayments.Checks;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || Array.IndexOf(args, "--help") >= 0)
    {
        Console.WriteLine(
            "op-checks --db <url> --effects <path> --stage <phase0|phase1|phase2|phase3> "
            + "[--prom <url> --from <t> --to <t>] --out <file.json>");
        return 0;
    }

    string? db = null;
    string? effects = null;
    string? stage = null;
    string? prom = null;
    string? output = null;
    DateTimeOffset? from = null;
    DateTimeOffset? to = null;

    for (var index = 0; index < args.Length; index++)
    {
        var argument = args[index];
        string Next() => index + 1 < args.Length
            ? args[++index]
            : throw new ArgumentException($"Missing value for {argument}.");

        switch (argument)
        {
            case "--db":
                db = Next();
                break;
            case "--effects":
                effects = Next();
                break;
            case "--stage":
                stage = Next();
                break;
            case "--prom":
                prom = Next();
                break;
            case "--from":
                from = ParseTime(Next());
                break;
            case "--to":
                to = ParseTime(Next());
                break;
            case "--out":
                output = Next();
                break;
            default:
                throw new ArgumentException($"Unknown argument {argument}.");
        }
    }

    if (db is null || stage is null)
    {
        Console.Error.WriteLine("--db and --stage are required.");
        return 2;
    }

    IReadOnlyList<CheckResult> results;
    try
    {
        results = await CheckRunner.RunAsync(new CheckOptions
        {
            Db = db,
            Effects = effects,
            Stage = stage,
            Prom = prom,
            From = from,
            To = to,
        });
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
    var json = JsonSerializer.Serialize(results, options);
    if (output is not null)
    {
        await File.WriteAllTextAsync(output, json);
    }

    Console.WriteLine(json);
    return results.Any(result => result.Applicable && !result.Passed) ? 1 : 0;
}

static DateTimeOffset ParseTime(string value) =>
    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
        ? parsed
        : DateTimeOffset.FromUnixTimeSeconds(long.Parse(value, CultureInfo.InvariantCulture));
