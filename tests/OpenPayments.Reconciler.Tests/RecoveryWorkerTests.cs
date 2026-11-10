using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class RecoveryWorkerTests(ReconcilerFixture db)
{
    [Fact]
    public async Task C44_replay_mode_resends_the_same_operation_and_applies_one_answer()
    {
        await db.ResetAsync();
        var (merchant, account, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'UNKNOWN', created_at = now() - interval '1 minute'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        var psp = new FakePsp(RecoveryMode.Replay, new PspResult.Approved("psp_recovered"));
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(psp, runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.Recovered);
        Assert.Equal(["authorize"], psp.Calls);
        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal(1, await db.CountAsync("select count(*) from ledger_transactions"));
    }

    [Fact]
    public async Task C45_past_replay_window_no_replay_is_sent()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'UNKNOWN', created_at = now() - interval '2 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        var psp = new FakePsp(RecoveryMode.Replay, new PspResult.Approved("psp_late"));
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(psp, runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.Recovered);
        Assert.Empty(psp.Calls);
        Assert.Equal("UNKNOWN", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Lookup_by_reference_is_used_when_the_adapter_declares_it_consistent()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set state = 'UNKNOWN', created_at = now() - interval '1 minute', psp_reference = 'psp_ref'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        var psp = new FakePsp(RecoveryMode.AuthoritativeLookup, new PspResult.Approved("psp_ref"));
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(psp, runner);

        await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(["lookup"], psp.Calls);
        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task C26_a_claim_leases_an_item_so_a_second_worker_does_not_take_it()
    {
        await db.ResetAsync();
        var (_, _, _, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'UNKNOWN', created_at = now() - interval '1 minute'
             where attempt_id = '{attempt}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new RecoveryStore(runner);
        var now = DateTimeOffset.UtcNow;

        var first = await store.ClaimAsync(now, 100, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), CancellationToken.None);
        var second = await store.ClaimAsync(now, 100, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
    }

    private static RecoveryWorker Worker(FakePsp psp, TxRunner runner)
    {
        return new RecoveryWorker(
            new PaymentStore(runner), new RecoveryStore(runner), psp, new AccountGate(),
            Options.Create(new ReconcilerOptions()), NullLogger<RecoveryWorker>.Instance);
    }

    private sealed class FakePsp(RecoveryMode mode, PspResult result) : IPspAdapterFactory, IPspAdapter
    {
        public List<string> Calls { get; } = [];

        public PspCapabilities Capabilities => new(
            SupportsSale: true, Unanswered: mode, ConsistentLookupByReference: true,
            ActionProof: ActionProof.ConfirmedCancel, KeyRetention: TimeSpan.FromHours(2),
            ReplayWindow: TimeSpan.FromMinutes(30), AuthorizationWindow: TimeSpan.FromDays(7),
            SettlementLag: TimeSpan.FromHours(1), LookupQuietWindow: TimeSpan.Zero);

        PspCapabilities IPspAdapterFactory.Capabilities(PspAccount account) => Capabilities;

        public IPspAdapter For(PspAccount account) => this;

        public Task<PspResult> AuthorizeAsync(AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct)
        {
            Calls.Add("authorize");
            return Task.FromResult(result);
        }

        public Task<PspResult> SaleAsync(AttemptId a, Money m, PaymentMethodRef p, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> CompleteActionAsync(AttemptId a, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> CaptureAsync(AttemptId a, Money m, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> VoidAsync(AttemptId a, AttemptId b, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> RefundAsync(RefundId r, Money m, CancellationToken ct) => throw new NotSupportedException();

        public Task<PspInquiry> RecoverAsync(AttemptId attempt, CancellationToken ct)
        {
            Calls.Add("recover");
            return Task.FromResult<PspInquiry>(new PspInquiry.Answered(result));
        }

        public Task<PspInquiry> RecoverRefundAsync(RefundId refund, CancellationToken ct) =>
            Task.FromResult<PspInquiry>(new PspInquiry.Answered(result));

        public Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct)
        {
            Calls.Add("lookup");
            return Task.FromResult<PspInquiry>(new PspInquiry.Answered(result));
        }

        public Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct) =>
            Task.FromResult<PspInquiry>(new PspInquiry.ProvenNotCharged("cancelled"));
    }
}
