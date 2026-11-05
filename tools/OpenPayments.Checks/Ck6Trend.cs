using System.Globalization;
using System.Text.Json;

namespace OpenPayments.Checks;

public static class Ck6Trend
{
    public static async Task<CheckResult> RunAsync(
        string prom,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string stage,
        CancellationToken cancellationToken)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddMinutes(-15);
        var query = stage == "phase3" ? "op_outbox_oldest_age_seconds" : "op_inflight";
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{prom.TrimEnd('/')}/api/v1/query_range?query={Uri.EscapeDataString(query)}"
            + $"&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step=15");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"Prometheus returned {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var samples = ReadSamples(document, query);
            if (samples.Count == 0)
            {
                return Failed($"Prometheus returned no samples for {query}.");
            }

            var first = samples[0];
            var last = samples[^1];
            var grew = stage == "phase3"
                ? last > Math.Max(first + 60, 300)
                : last > (first * 1.5) + 10;
            var violations = grew
                ? new[] { query }
                : [];
            return new CheckResult(
                "CK6",
                Applicable: true,
                Passed: !grew,
                RowsExamined: samples.Count,
                Violations: grew ? 1 : 0,
                SampleIds: violations,
                MoneyImpactMinor: 0,
                Reason: grew ? $"{query} grew from {first:F2} to {last:F2}." : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed(exception.Message);
        }
    }

    private static List<double> ReadSamples(JsonDocument document, string query)
    {
        var samples = new List<double>();
        var data = document.RootElement.GetProperty("data");
        if (!data.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
        {
            return samples;
        }

        var series = result[0];
        if (!series.TryGetProperty("metric", out var metric)
            || !metric.TryGetProperty("__name__", out var name)
            || name.GetString() != query)
        {
            return samples;
        }

        if (!series.TryGetProperty("values", out var values))
        {
            return samples;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.GetArrayLength() >= 2
                && double.TryParse(
                    value[1].GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                samples.Add(parsed);
            }
        }

        return samples;
    }

    private static CheckResult Failed(string reason) =>
        new("CK6", Applicable: true, Passed: false, RowsExamined: 0, Violations: 1, SampleIds: [], MoneyImpactMinor: 0, Reason: reason);
}
