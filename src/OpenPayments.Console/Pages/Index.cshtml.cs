using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;
using OpenPayments.Console.Presentation;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages;

/// <summary>
/// The home dashboard: throughput, correctness, reliability and the payment lifecycle. The
/// work queue itself is one click away at /attention; this page only reads.
/// </summary>
public sealed class IndexModel(
    IQueueReader queues,
    IDashboardReader dashboard,
    IOperationsMetrics metrics,
    IOptions<DashboardOptions> dashboardOptions,
    IOptions<MetricsOptions> metricsOptions,
    TimeProvider time) : PageModel
{
    public AttentionQueues? Attention { get; private set; }

    public DashboardSnapshot? Operations { get; private set; }

    public ThroughputSnapshot? Throughput { get; private set; }

    public string? AttentionMessage { get; private set; }

    public string? OperationsMessage { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public OpenPayments.Console.Security.Staff Person { get; private set; } = null!;

    public IReadOnlyList<QueueView> Waiting =>
        Attention is null ? [] : [.. Attention.Queues.Where(queue => queue.Count > 0)];

    public DateTimeOffset CheckedAt => Now - (Attention?.Lag ?? TimeSpan.Zero);

    public string LifecycleWindow =>
        AttentionPresentation.Duration(dashboardOptions.Value.LifecycleWindow);

    public string MetricsWindow =>
        AttentionPresentation.Duration(metricsOptions.Value.Window);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        Person = CurrentStaff.Get(HttpContext);

        // Start all three reads before awaiting, so a slow Prometheus call doesn't add to the
        // database reads. Each failure is local to its panel.
        var attention = queues.ReadAsync(Now, ct);
        var operations = dashboard.ReadAsync(Now, ct);
        var throughput = metrics.ReadAsync(ct);

        try
        {
            Attention = await attention;
        }
        catch (ReplicaUnavailableException)
        {
            AttentionMessage = "The read replica can't be reached, so the attention counts are unavailable.";
        }
        catch (ReadTimeoutException)
        {
            AttentionMessage = "The attention counts took too long to read. Try again in a moment.";
        }

        try
        {
            Operations = await operations;
        }
        catch (ReplicaUnavailableException)
        {
            OperationsMessage = "The read replica can't be reached, so lifecycle and correctness are unavailable.";
        }
        catch (ReadTimeoutException)
        {
            OperationsMessage = "Lifecycle and correctness took too long to read. Try again in a moment.";
        }

        try
        {
            Throughput = await throughput;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Throughput = null;
        }
    }
}
