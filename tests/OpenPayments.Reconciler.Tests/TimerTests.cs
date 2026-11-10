using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class TimerTests(ReconcilerFixture db)
{
    [Fact]
    public async Task Action_window_end_moves_to_unknown_never_failed()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set state = 'ACTION_REQUIRED', action_expires_at = now() - interval '1 minute'
             where attempt_id = '{attempt}';
            update payments set state = 'ACTION_REQUIRED' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        using var worker = Worker(store);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.ExpiredActions);
        Assert.Equal("UNKNOWN", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal("UNKNOWN", await db.TextAsync(
            $"select state from payment_attempts where attempt_id = '{attempt}'"));
        Assert.Equal(0, await db.CountAsync("select count(*) from ledger_transactions"));
    }

    [Fact]
    public async Task Payment_window_end_cancels_awaiting_method()
    {
        await db.ResetAsync();
        var (_, _, payment, _) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payments
               set state = 'AWAITING_METHOD', expires_at = now() - interval '1 minute'
             where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        using var worker = Worker(store);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.CanceledPayments);
        Assert.Equal("CANCELED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from outbox where aggregate_id = '{payment}' and event_type = 'payment.canceled'"));
    }

    [Fact]
    public async Task Authorization_window_end_expires_and_releases_the_hold()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'SUCCEEDED' where attempt_id = '{attempt}';
            update payments
               set state = 'AUTHORIZED', authorized_until = now() - interval '1 minute'
             where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        using var worker = Worker(store);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.ExpiredAuthorizations);
        Assert.Equal("EXPIRED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{payment}' and kind = 'expiry'"));
    }

    [Fact]
    public async Task Authorization_window_passes_during_live_capture_and_expiry_waits()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        var capture = Guid.NewGuid();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'SUCCEEDED' where attempt_id = '{attempt}';
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, method_ref, method_type)
            select '{capture}', payment_id, merchant_id, 'capture', psp, psp_account_id, 'PENDING', 'tok', 'card'
              from payment_attempts where attempt_id = '{attempt}';
            update payments
               set state = 'AUTHORIZED', authorized_until = now() - interval '1 minute'
             where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        using var worker = Worker(store);

        var first = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, first.ExpiredAuthorizations);
        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));

        await db.ExecAdminAsync(
            $"update payment_attempts set state = 'FAILED' where attempt_id = '{capture}'");
        var second = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, second.ExpiredAuthorizations);
        Assert.Equal("EXPIRED", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Timer_and_manual_capture_cannot_both_win()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts set state = 'SUCCEEDED' where attempt_id = '{attempt}';
            update payments
               set state = 'AUTHORIZED', authorized_until = now() - interval '1 minute'
             where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var amount = new Money(1000, CurrencyCode.Usd);
        var applied = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            new PaymentId(payment),
            new PaymentCommand.Capture(new AttemptId(Guid.NewGuid()), amount, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));
        var capture = applied.Payment.Attempt!.Id;
        await store.ApplyAsync(
            new PaymentId(payment),
            new PaymentCommand.CaptureAnswer(capture, true, amount, null, false, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);
        using var worker = Worker(store);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.ExpiredAuthorizations);
        Assert.Equal("CAPTURED", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    private static TimerWorker Worker(PaymentStore store) =>
        new(store, Options.Create(new ReconcilerOptions()), NullLogger<TimerWorker>.Instance);
}
