using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class EscalationTests(ReconcilerFixture db)
{
    [Fact]
    public async Task Unknown_sale_past_age_limit_moves_to_review_with_no_ledger()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set operation = 'sale', state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.EscalatedAttempts);
        Assert.Equal("REVIEW", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal("REVIEW", await db.TextAsync(
            $"select state from payment_attempts where attempt_id = '{attempt}'"));
        Assert.Equal(0, await db.CountAsync("select count(*) from ledger_transactions"));
    }

    [Fact]
    public async Task Unknown_capture_past_age_limit_moves_authorized_payment_to_review()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set operation = 'capture', state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'AUTHORIZED' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.EscalatedAttempts);
        Assert.Equal("REVIEW", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Unknown_refund_past_age_limit_moves_refund_only()
    {
        await db.ResetAsync();
        var (merchant, account, _, _) = await db.SeedPendingAsync();
        var (payment, refund) = await db.SeedUnknownRefundAsync(merchant, account);
        await db.ExecAdminAsync(
            $"update refunds set created_at = now() - interval '3 hours' where id = '{refund}'");
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, summary.EscalatedRefunds);
        Assert.Equal("REVIEW", await db.TextAsync($"select state from refunds where id = '{refund}'"));
        Assert.Equal("CAPTURED", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Stuck_void_is_never_escalated()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set operation = 'void', state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'AUTHORIZED' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.EscalatedAttempts);
        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Attempt_resolved_just_before_escalation_is_not_moved()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        using var worker = Worker(store, runner);
        worker.BeforeApply = () =>
        {
            using var connection = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
            connection.Open();
            using var command = new Npgsql.NpgsqlCommand(
                $"update payment_attempts set state = 'SUCCEEDED' where attempt_id = '{attempt}'",
                connection);
            command.ExecuteNonQuery();
        };

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.EscalatedAttempts);
        Assert.Equal("UNKNOWN", await db.TextAsync($"select state from payments where id = '{payment}'"));
    }

    [Fact]
    public async Task Escalation_leaves_key_unresolved()
    {
        await db.ResetAsync();
        var (merchant, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            insert into idempotency_keys
              (merchant_id, key, request_hash, payment_id, attempt_id)
            values ('{merchant}', 'k1', '\\x00', '{payment}', '{attempt}');
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, await db.CountAsync(
            $"select count(*) from idempotency_keys where merchant_id = '{merchant}' and resolved_at is not null"));
    }

    [Fact]
    public async Task Escalated_case_appears_in_needs_review_queue()
    {
        await db.ResetAsync();
        var (_, _, payment, attempt) = await db.SeedPendingAsync();
        await db.ExecAdminAsync($"""
            update payment_attempts
               set state = 'UNKNOWN', created_at = now() - interval '3 hours'
             where attempt_id = '{attempt}';
            update payments set state = 'UNKNOWN' where id = '{payment}';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(new PaymentStore(runner), runner);

        await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, await db.CountAsync("select count(*) from payments where state = 'REVIEW'"));
    }

    private static EscalationWorker Worker(PaymentStore store, TxRunner runner) =>
        new(store, runner, Options.Create(new ReconcilerOptions()), NullLogger<EscalationWorker>.Instance);
}
