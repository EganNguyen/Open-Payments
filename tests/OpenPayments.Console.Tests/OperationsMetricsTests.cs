using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;
using Xunit;

namespace OpenPayments.Console.Tests;

// The Prometheus reader behind the throughput and reliability panels.
public sealed class OperationsMetricsTests
{
    [Fact]
    public async Task Reads_scalars_and_chooses_the_largest_rejection_reason()
    {
        using var handler = new FakePrometheusHandler(query => query switch
        {
            var q when q.Contains("sum by (reason)", StringComparison.Ordinal) => JsonResponse(
                """[{"metric":{"reason":"busy"},"value":[1,"0.2"]},{"metric":{"reason":"rate_limit"},"value":[1,"1.5"]}]"""),
            var q when q.Contains("op_payments_completed_total", StringComparison.Ordinal) => JsonResponse(Vector("12.5")),
            var q when q.Contains("op_requests_admitted_total", StringComparison.Ordinal) => JsonResponse(Vector("20")),
            var q when q.Contains("op_requests_rejected_total", StringComparison.Ordinal) => JsonResponse(Vector("1.5")),
            var q when q.Contains("op_platform_overhead_seconds_bucket", StringComparison.Ordinal) && q.Contains("0.50", StringComparison.Ordinal) => JsonResponse(Vector("0.005")),
            var q when q.Contains("op_platform_overhead_seconds_bucket", StringComparison.Ordinal) && q.Contains("0.95", StringComparison.Ordinal) => JsonResponse(Vector("0.020")),
            var q when q.Contains("op_platform_overhead_seconds_bucket", StringComparison.Ordinal) => JsonResponse(Vector("0.050")),
            var q when q.Contains("op_psp_wait_seconds_bucket", StringComparison.Ordinal) => JsonResponse(Vector("0.250")),
            var q when q.Contains("op_inflight", StringComparison.Ordinal) => JsonResponse(Vector("7")),
            var q when q.Contains("pg_stat_database_deadlocks", StringComparison.Ordinal) => JsonResponse(Vector("0.01")),
            var q when q.Contains("pgbouncer_pools_client_maxwait_seconds", StringComparison.Ordinal) => JsonResponse(Vector("0.002")),
            var q when q.Contains("op_outbox_oldest_age_seconds", StringComparison.Ordinal) => JsonResponse(Vector("4")),
            _ => JsonResponse("[]"),
        });
        var reader = Reader(handler, "http://prometheus.test");

        var snapshot = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(12.5, snapshot.CompletedPerSecond);
        Assert.Equal(20, snapshot.AdmittedPerSecond);
        Assert.Equal(1.5, snapshot.RejectedPerSecond);
        Assert.Equal(0.005, snapshot.OverheadP50);
        Assert.Equal(0.02, snapshot.OverheadP95);
        Assert.Equal(0.05, snapshot.OverheadP99);
        Assert.Equal(0.25, snapshot.PspWaitP99);
        Assert.Equal(7, snapshot.InFlight);
        Assert.Equal(0.01, snapshot.DeadlocksPerSecond);
        Assert.Equal(0.002, snapshot.PgBouncerMaxWait);
        Assert.Equal(4, snapshot.OutboxOldestAge);
        Assert.Equal("rate_limit", snapshot.TopRejectReason);
        Assert.Equal(1.5, snapshot.TopRejectPerSecond);
    }

    [Fact]
    public async Task No_prometheus_url_means_no_metrics()
    {
        using var handler = new FakePrometheusHandler(_ => JsonResponse(Vector("1")));
        var reader = Reader(handler, string.Empty);

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_queries_do_not_turn_into_zeroes()
    {
        using var handler = new FakePrometheusHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var reader = Reader(handler, "http://prometheus.test");

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_series_stays_null()
    {
        using var handler = new FakePrometheusHandler(query => query.Contains("op_outbox_oldest_age_seconds", StringComparison.Ordinal)
            ? JsonResponse("[]")
            : JsonResponse(Vector("1")));
        var reader = Reader(handler, "http://prometheus.test");

        var snapshot = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.OutboxOldestAge);
        Assert.Equal(1, snapshot.CompletedPerSecond);
    }

    private static PrometheusOperationsMetrics Reader(HttpMessageHandler handler, string url)
    {
#pragma warning disable CA2000 // The reader doesn't own the client; the test process is short-lived.
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://prometheus.test/"),
            Timeout = TimeSpan.FromSeconds(2),
        };
#pragma warning restore CA2000
        return new PrometheusOperationsMetrics(
            client,
            Options.Create(new MetricsOptions { PrometheusUrl = url }),
            NullLogger<PrometheusOperationsMetrics>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string result) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"status":"success","data":{"resultType":"vector","result":RESULT}}"""
                    .Replace("RESULT", result, StringComparison.Ordinal)),
        };

    private static string Vector(string value) =>
        """[{"metric":{},"value":[1,"VALUE"]}]""".Replace("VALUE", value, StringComparison.Ordinal);
}

file sealed class FakePrometheusHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(Uri.UnescapeDataString(request.RequestUri!.Query)));
}
