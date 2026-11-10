using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenPayments.Psp.Resilience;
using OpenPayments.Transitions;

namespace OpenPayments.Api;

/// <summary>
/// The four application metrics the API contracts on, plus the in-flight gauge and PSP wait
/// histogram the dashboard and CK6 use. The values are process-local and are exposed on
/// GET /metrics in the Prometheus text format.
/// </summary>
public sealed class PaymentMetrics(TxRunner runner)
{
    private static readonly double[] PlatformOverheadBuckets =
    [
        0.001, 0.0025, 0.005, 0.01, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05,
        0.06, 0.075, 0.1, 0.2, 0.5, 1,
    ];

    private static readonly double[] PspWaitBuckets =
    [
        0.01, 0.025, 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2.5, 5, 10,
    ];

    private readonly ConcurrentDictionary<string, long> _completedOutcomes = new();
    private readonly ConcurrentDictionary<string, long> _rejectedReasons = new();
    private readonly Histogram _platformOverhead = new(PlatformOverheadBuckets);
    private readonly Histogram _pspWait = new(PspWaitBuckets);
    private long _completed;
    private long _admitted;
    private long _inflight;
    private DateTime _started = DateTime.UtcNow;
    private int _startGen2 = GC.CollectionCount(2);
    private long _startAllocated = GC.GetTotalAllocatedBytes();

    /// <summary>Set by the API at startup so the PSP gate's series are on /metrics.</summary>
    public AccountGate? PspGate { get; set; }

    /// <summary>Set at startup so the callback event series are on /metrics.</summary>
    public PspEventStore? PspEvents { get; set; }

    public long CompletedPayments => Interlocked.Read(ref _completed);

    public long InFlight => Interlocked.Read(ref _inflight);

    public void RequestStarted() => Interlocked.Increment(ref _inflight);

    public void RequestFinished(TimeSpan total, TimeSpan pspWait)
    {
        Interlocked.Decrement(ref _inflight);
        var overhead = total - pspWait;
        _platformOverhead.Observe(Math.Max(0, overhead.TotalSeconds));
        _pspWait.Observe(Math.Max(0, pspWait.TotalSeconds));
    }

    public void RequestAdmitted() => Interlocked.Increment(ref _admitted);

    public void RequestRejected(string reason) =>
        _rejectedReasons.AddOrUpdate(reason, 1, static (_, count) => count + 1);

    public void Completed(string outcome)
    {
        Interlocked.Increment(ref _completed);
        _completedOutcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
    }

    // Kept for the existing unit assertion in CaptureTests.
    public void CompletedPayment() => Completed("captured");

    public void Reset()
    {
        Interlocked.Exchange(ref _completed, 0);
        Interlocked.Exchange(ref _admitted, 0);
        Interlocked.Exchange(ref _inflight, 0);
        _completedOutcomes.Clear();
        _rejectedReasons.Clear();
        _platformOverhead.Reset();
        _pspWait.Reset();
        _started = DateTime.UtcNow;
        _startGen2 = GC.CollectionCount(2);
        _startAllocated = GC.GetTotalAllocatedBytes();
    }

    public string Render()
    {
        var text = new StringBuilder();

        AppendCounter(text, "op_requests_admitted_total", "Requests admitted by the API edge.", _admitted);
        AppendLabeledCounter(
            text,
            "op_requests_rejected_total",
            "Requests refused before work, by reason.",
            "reason",
            _rejectedReasons);
        AppendLabeledCounter(
            text,
            "op_payments_completed_total",
            "Payments that reached a final committed outcome, by outcome.",
            "outcome",
            _completedOutcomes);
        AppendHistogram(
            text,
            "op_platform_overhead_seconds",
            "Total request time minus PSP wait.",
            _platformOverhead);
        AppendHistogram(
            text,
            "op_psp_wait_seconds",
            "Time spent inside the PSP adapter call.",
            _pspWait);
        AppendGauge(text, "op_inflight", "Requests currently in flight on this instance.", InFlight);
        AppendLabeledCounter(
            text,
            "op_tx_retries_total",
            "Transaction retries by SQLSTATE.",
            "sqlstate",
            runner.Metrics.Retries);
        AppendCounter(
            text,
            "op_conditional_update_zero_rows_total",
            "Conditional updates that changed no row.",
            runner.Metrics.ConditionalUpdateZeroRows);
        AppendGauge(
            text,
            "op_abandoned_commits",
            "Commits still running after the wait limit.",
            runner.Metrics.AbandonedCommits);
        AppendCounter(
            text,
            "op_commit_warnings_total",
            "Commit-time replication warnings.",
            runner.Metrics.CommitWarnings);

        var process = Process.GetCurrentProcess();
        AppendCounter(
            text,
            "process_cpu_seconds_total",
            "Total process CPU time in seconds.",
            (long)process.TotalProcessorTime.TotalSeconds);
        AppendGauge(text, "process_resident_memory_bytes", "Resident memory in bytes.", process.WorkingSet64);
        AppendGauge(text, "process_num_threads", "Operating-system threads.", process.Threads.Count);
        AppendGauge(text, "op_threadpool_queue", "Pending ThreadPool work items.", ThreadPool.PendingWorkItemCount);
        var elapsed = Math.Max((DateTime.UtcNow - _started).TotalSeconds, 0.001);
        AppendGauge(
            text,
            "op_gen2_gc_per_second",
            "Gen 2 garbage collections per second since start.",
            (long)Math.Round((GC.CollectionCount(2) - _startGen2) / elapsed));
        AppendGauge(
            text,
            "op_allocation_rate_bytes_per_second",
            "Managed allocation rate since start.",
            (long)Math.Round((GC.GetTotalAllocatedBytes() - _startAllocated) / elapsed));

                if (PspEvents is not null)
        {
            foreach (var (outcomeKey, count) in PspEvents.OutcomeCounts)
            {
                var parts = outcomeKey.Split(':', 2);
                text.Append("op_psp_events_total{source=\"").Append(Escape(parts[0]))
                    .Append("\",outcome=\"").Append(Escape(parts[1])).Append("\"} ")
                    .Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            text.Append("op_psp_events_parked ").Append(PspEvents.ParkedCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (PspEvents.OldestParkedAt is { } oldest)
            {
                var age = Math.Max(0, (DateTimeOffset.UtcNow - oldest).TotalSeconds);
                text.Append("op_psp_events_parked_oldest_age_seconds ")
                    .Append(age.ToString("F0", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        if (PspGate is not null)
        {
            foreach (var (reason, count) in PspGate.RejectedCounts)
            {
                AppendLabeledCounter(
                    text, "op_psp_rejected_total", "PSP calls refused locally, by reason.", "reason",
                    [new KeyValuePair<string, long>(reason, count)]);
            }

            foreach (var (account, psp, open) in PspGate.Breakers())
            {
                text.Append("op_psp_breaker_state{psp=\"").Append(Escape(psp))
                    .Append("\",account=\"").Append(account).Append("\"} ")
                    .Append(open ? '1' : '0').Append('\n');
            }

            foreach (var (psp, open) in PspGate.PspBreakers())
            {
                text.Append("op_psp_breaker_state{psp=\"").Append(Escape(psp))
                    .Append("\",account=\"\"} ")
                    .Append(open ? '1' : '0').Append('\n');
            }
        }

return text.ToString();
    }

    private static void AppendCounter(StringBuilder text, string name, string help, long value)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(" counter\n");
        text.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendLabeledCounter(
        StringBuilder text,
        string name,
        string help,
        string label,
        IEnumerable<KeyValuePair<string, long>> values)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(" counter\n");
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(name)
                .Append('{').Append(label).Append("=\"").Append(Escape(key)).Append("\"} ")
                .Append(value.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }

    private static void AppendGauge(StringBuilder text, string name, string help, long value)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(" gauge\n");
        text.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendHistogram(
        StringBuilder text,
        string name,
        string help,
        Histogram histogram)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(" histogram\n");
        var cumulative = 0L;
        for (var index = 0; index < histogram.Bounds.Length; index++)
        {
            cumulative += histogram.Bucket(index);
            text.Append(name).Append("_bucket{le=\"")
                .Append(histogram.Bounds[index].ToString("0.####", CultureInfo.InvariantCulture))
                .Append("\"} ")
                .Append(cumulative.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        cumulative += histogram.Bucket(histogram.Bounds.Length);
        text.Append(name).Append("_bucket{le=\"+Inf\"} ")
            .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(name).Append("_sum ")
            .Append(histogram.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
        text.Append(name).Append("_count ")
            .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Histogram(double[] bounds)
    {
        private readonly long[] _buckets = new long[bounds.Length + 1];
        private long _sumMicros;
        private long _count;

        public double[] Bounds { get; } = bounds;

        public double SumSeconds => Interlocked.Read(ref _sumMicros) / 1_000_000d;

        public long Bucket(int index) => Interlocked.Read(ref _buckets[index]);

        public void Observe(double seconds)
        {
            var index = Array.FindIndex(Bounds, bound => seconds <= bound);
            if (index < 0)
            {
                index = Bounds.Length;
            }

            Interlocked.Increment(ref _buckets[index]);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumMicros, (long)Math.Round(seconds * 1_000_000));
        }

        public void Reset()
        {
            Array.Clear(_buckets);
            Interlocked.Exchange(ref _sumMicros, 0);
            Interlocked.Exchange(ref _count, 0);
        }
    }
}

/// <summary>
/// Carries the request's PSP wait from the flow to the edge middleware without changing every
/// public flow signature. AsyncLocal keeps it per request.
/// </summary>
public sealed class RequestTelemetry
{
    private readonly AsyncLocal<Scope?> _current = new();

    public Scope Begin()
    {
        var scope = new Scope();
        _current.Value = scope;
        return scope;
    }

    public void End() => _current.Value = null;

    public PspWaitScope MeasurePspWait() => new(this);

    private void Add(TimeSpan elapsed) => _current.Value?.Add(elapsed);

    public sealed class Scope
    {
        private long _pspWaitTicks;

        public TimeSpan PspWait => TimeSpan.FromTicks(Interlocked.Read(ref _pspWaitTicks));

        public void Add(TimeSpan elapsed) =>
            Interlocked.Add(ref _pspWaitTicks, elapsed.Ticks);
    }

    public sealed class PspWaitScope : IDisposable
    {
        private readonly RequestTelemetry _owner;
        private readonly long _started = Stopwatch.GetTimestamp();

        public PspWaitScope(RequestTelemetry owner) => _owner = owner;

        public void Dispose() =>
            _owner.Add(Stopwatch.GetElapsedTime(_started));
    }
}
