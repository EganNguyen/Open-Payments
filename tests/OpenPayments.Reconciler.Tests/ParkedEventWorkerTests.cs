using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class ParkedEventWorkerTests(ReconcilerFixture db)
{
    [Fact]
    public void Reconciler_references_no_api_or_console()
    {
        var references = typeof(ParkedEventWorker).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToList();
        Assert.DoesNotContain("OpenPayments.Api", references);
        Assert.DoesNotContain("OpenPayments.Console", references);
    }

    [Fact]
    public void Startup_refuses_key_retention_below_24h()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RECONCILER_KEY_RETENTION_HOURS"] = "1",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ReconcilerOptions.FromConfiguration(configuration));
    }

    [Fact]
    public async Task Parked_event_applies_once_its_operation_can_take_it()
    {
        await db.ResetAsync();
        var (_, account, payment, attempt) = await db.SeedPendingAsync();
        await db.InsertCallbackEventAsync(account, attempt, "evt_apply", DateTimeOffset.UtcNow);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var worker = new ParkedEventWorker(
            store, runner, Options.Create(new ReconcilerOptions()),
            NullLogger<ParkedEventWorker>.Instance);

        var applied = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, applied);
        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal("applied", await db.TextAsync("select outcome from psp_events where psp_event_id = 'evt_apply'"));
    }

    [Fact]
    public async Task Parked_event_past_limit_expires_and_leaves_the_operation_unresolved()
    {
        await db.ResetAsync();
        var (_, account, payment, attempt) = await db.SeedPendingAsync();
        await db.InsertCallbackEventAsync(account, attempt, "evt_old", DateTimeOffset.UtcNow.AddMinutes(-20));
        await using var runner = new TxRunner(db.AppConnectionString);
        var worker = new ParkedEventWorker(
            new PaymentStore(runner), runner, Options.Create(new ReconcilerOptions()),
            NullLogger<ParkedEventWorker>.Instance);

        var applied = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, applied);
        Assert.Equal("PSP_PENDING", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal("expired", await db.TextAsync("select outcome from psp_events where psp_event_id = 'evt_old'"));
    }

    [Fact]
    public async Task Api_answer_parked_by_L7_is_applied_by_the_worker()
    {
        await db.ResetAsync();
        var (_, account, payment, attempt) = await db.SeedPendingAsync();
        var amount = new Money(1000, CurrencyCode.Usd);
        var command = new PaymentCommand.PspAnswer(
            new AttemptId(attempt), AttemptOperation.Authorize, PspAnswerOutcome.Approved,
            amount, null, null, DateTimeOffset.UtcNow);
        var payload = ParkedAnswer.From(command).Serialize();
        await db.ExecAdminAsync($"""
            insert into psp_events
              (psp_account_id, psp_event_id, source, payload, payment_id, attempt_id, next_attempt_at)
            values ('{account}', 'api:attempt:{attempt}', 'api_answer', '{payload}'::jsonb,
                    '{payment}', '{attempt}', now() - interval '1 second');
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        var worker = new ParkedEventWorker(
            new PaymentStore(runner), runner, Options.Create(new ReconcilerOptions()),
            NullLogger<ParkedEventWorker>.Instance);

        await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("AUTHORIZED", await db.TextAsync($"select state from payments where id = '{payment}'"));
        Assert.Equal(1, await db.CountAsync("select count(*) from ledger_transactions"));
    }
}
