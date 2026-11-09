using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenPayments.Console.Data;

/// <summary>
/// The throughput and reliability numbers Prometheus already holds. A missing series is null,
/// never zero, so an unmeasured stage can't look healthy.
/// </summary>
public sealed record ThroughputSnapshot(
    double? CompletedPerSecond,
    double? AdmittedPerSecond,
    double? RejectedPerSecond,
    double? OverheadP50,
    double? OverheadP95,
    double? OverheadP99,
    double? PspWaitP99,
    double? InFlight,
    double? DeadlocksPerSecond,
    double? PgBouncerMaxWait,
    double? OutboxOldestAge,
    string? TopRejectReason,
    double? TopRejectPerSecond);

public interface IOperationsMetrics
{
    /// <summary>Null when Prometheus isn't configured or no series answered.</summary>
    Task<ThroughputSnapshot?> ReadAsync(CancellationToken ct);
}

public sealed class MetricsOptions
{
    /// <summary>For example http://prometheus:9090. Empty disables the metrics panels.</summary>
    public string PrometheusUrl { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class PrometheusOperationsMetrics(
    HttpClient client,
    IOptions<MetricsOptions> options,
    ILogger<PrometheusOperationsMetrics> logger) : IOperationsMetrics
{
    public async Task<ThroughputSnapshot?> ReadAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.PrometheusUrl) || client.BaseAddress is null)
        {
            return null;
        }

        var window = Math.Max(1, (int)settings.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        var tasks = new
        {
            Completed = ScalarAsync($"sum(rate(op_payments_completed_total[{window}]))", ct),
            Admitted = ScalarAsync($"sum(rate(op_requests_admitted_total[{window}]))", ct),
            Rejected = ScalarAsync($"sum(rate(op_requests_rejected_total[{window}]))", ct),
            OverheadP50 = ScalarAsync($"histogram_quantile(0.50, sum(rate(op_platform_overhead_seconds_bucket[{window}])) by (le))", ct),
            OverheadP95 = ScalarAsync($"histogram_quantile(0.95, sum(rate(op_platform_overhead_seconds_bucket[{window}])) by (le))", ct),
            OverheadP99 = ScalarAsync($"histogram_quantile(0.99, sum(rate(op_platform_overhead_seconds_bucket[{window}])) by (le))", ct),
            PspWaitP99 = ScalarAsync($"histogram_quantile(0.99, sum(rate(op_psp_wait_seconds_bucket[{window}])) by (le))", ct),
            InFlight = ScalarAsync("sum(op_inflight)", ct),
            Deadlocks = ScalarAsync($"sum(rate(pg_stat_database_deadlocks{{datname=\"openpayments\"}}[{window}]))", ct),
            PgBouncerWait = ScalarAsync("max(pgbouncer_pools_client_maxwait_seconds)", ct),
            OutboxAge = ScalarAsync("max(op_outbox_oldest_age_seconds)", ct),
            TopReason = TopReasonAsync($"sum by (reason) (rate(op_requests_rejected_total[{window}]))", ct),
        };

        var snapshot = new ThroughputSnapshot(
            await tasks.Completed,
            await tasks.Admitted,
            await tasks.Rejected,
            await tasks.OverheadP50,
            await tasks.OverheadP95,
            await tasks.OverheadP99,
            await tasks.PspWaitP99,
            await tasks.InFlight,
            await tasks.Deadlocks,
            await tasks.PgBouncerWait,
            await tasks.OutboxAge,
            (await tasks.TopReason)?.Reason,
            (await tasks.TopReason)?.PerSecond);

        var any = snapshot.CompletedPerSecond is not null
            || snapshot.AdmittedPerSecond is not null
            || snapshot.RejectedPerSecond is not null
            || snapshot.OverheadP99 is not null
            || snapshot.PspWaitP99 is not null
            || snapshot.InFlight is not null
            || snapshot.DeadlocksPerSecond is not null
            || snapshot.OutboxOldestAge is not null;
        if (!any)
        {
            logger.LogWarning("dashboard_metrics_unavailable url={Url}", settings.PrometheusUrl);
            return null;
        }

        return snapshot;
    }

    private async Task<double?> ScalarAsync(string query, CancellationToken ct)
    {
        try
        {
            using var result = await QueryAsync(query, ct);
            if (result is null)
            {
                return null;
            }

            var data = result.RootElement.GetProperty("data");
            if (!data.TryGetProperty("result", out var rows) || rows.GetArrayLength() == 0)
            {
                return null;
            }

            var value = rows[0].GetProperty("value")[1].GetString();
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<(string Reason, double PerSecond)?> TopReasonAsync(string query, CancellationToken ct)
    {
        try
        {
            using var result = await QueryAsync(query, ct);
            if (result is null)
            {
                return null;
            }

            var data = result.RootElement.GetProperty("data");
            if (!data.TryGetProperty("result", out var rows))
            {
                return null;
            }

            (string Reason, double PerSecond)? top = null;
            foreach (var row in rows.EnumerateArray())
            {
                var reason = row.TryGetProperty("metric", out var metric) && metric.TryGetProperty("reason", out var label)
                    ? label.GetString()
                    : null;
                var text = row.GetProperty("value")[1].GetString();
                if (reason is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                if (top is null || value > top.Value.PerSecond)
                {
                    top = (reason, value);
                }
            }

            return top;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<JsonDocument?> QueryAsync(string query, CancellationToken ct)
    {
        using var response = await client.GetAsync("api/v1/query?query=" + Uri.EscapeDataString(query), ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "success", StringComparison.Ordinal))
        {
            document.Dispose();
            return null;
        }

        return document;
    }
}
