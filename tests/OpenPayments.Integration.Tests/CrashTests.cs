using System.Net;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace OpenPayments.Integration.Tests;

// Process crashes at the money path's dangerous moments, with the real API and mock PSP. Each
// test checks the Stage 1 invariants: one PSP effect per logical payment, a balanced ledger, the
// attempt findable at the PSP by the ID written before the call, and a same-key retry that never
// calls the PSP again.
[Collection("c43")]
public sealed class CrashTests(C43Fixture db)
{
    private static readonly object Sale = new
    {
        amount_minor = 1000,
        currency = "USD",
        capture_mode = "auto",
        method = new { type = "card", @ref = "tok_test" },
    };

    [Fact]
    public async Task K1_crash_after_the_psp_charged_and_before_recording_keeps_the_attempt_findable()
    {
        // The mock approves, records the charge, then never answers, so the API is waiting on the
        // PSP when it dies: the charge exists only at the PSP.
        await using var harness = await CrashHarness.StartAsync(db, new { approve_then_hang_rate = 1.0 });
        var request = harness.PostAsync("/v1/payments", "k1", Sale);
        await harness.WaitForMockStatAsync("effects", 1);
        harness.CrashApi();
        await Assert.ThrowsAnyAsync<Exception>(() => request);

        // Transaction A survived: the payment and its attempt are durable, still in flight.
        var (paymentId, attemptId, paymentState, attemptState) = await SingleAttemptAsync();
        Assert.Equal("PSP_PENDING", paymentState);
        Assert.Equal("PENDING", attemptState);
        // The attempt's ID is the PSP's key, so Stage 2 can learn the outcome (D27).
        Assert.Equal("approved", await harness.InquireAsync(attemptId));
        Assert.Equal(0L, await ScalarAsync($"select count(*) from ledger_entries e join ledger_transactions t on t.id = e.txn_id where t.payment_id = '{paymentId}'"));

        await harness.StartApiAsync();
        var requests = await harness.MockStatAsync("requests");

        // E2: soon after, the key may still belong to a running request.
        using (var early = await harness.PostAsync("/v1/payments", "k1", Sale))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        }

        // Past the in-flight window, the retry renders the state, and the PSP isn't called again.
        await AgeKeyAsync("k1");
        using var retry = await harness.PostAsync("/v1/payments", "k1", Sale);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(paymentId, await IdAsync(retry));
        Assert.Equal(requests, await harness.MockStatAsync("requests"));
        Assert.Equal(1L, await harness.MockStatAsync("effects"));
        Assert.Equal(1L, await ScalarAsync("select count(*) from payments"));
    }

    [Fact]
    public async Task K2_crash_during_a_late_commit_after_the_psp_answer_keeps_the_committed_outcome()
    {
        // The PSP answers after 1.5 s. Once transaction A has committed, the standby is paused,
        // so transaction B's COMMIT waits in SyncRep, and the API dies while it waits.
        await using var harness = await CrashHarness.StartAsync(db, new { latency_ms = 1500 });
        var request = harness.PostAsync("/v1/payments", "k2", Sale);
        await WaitForAsync("select count(*) from payment_attempts", 1);
        await db.BlockAsync();
        Assert.True(await db.WaitForSyncRepWaiterAsync(), "Transaction B never reached COMMIT.");
        harness.CrashApi();
        await Assert.ThrowsAnyAsync<Exception>(() => request);

        // The commit was durable on the primary; when the standby returns it is replicated (D28).
        await db.UnblockAsync(terminateBackends: false);
        var (paymentId, attemptId, paymentState, attemptState) = await SingleAttemptAsync();
        Assert.Equal("CAPTURED", paymentState);
        Assert.Equal("SUCCEEDED", attemptState);
        Assert.Equal("approved", await harness.InquireAsync(attemptId));
        Assert.Equal(0L, await UnbalancedAsync());
        // The answer was never stored, so the key stays unresolved and can't expire (D19).
        Assert.Equal(0L, await ScalarAsync("select count(*) from idempotency_keys where key = 'k2' and resolved_at is not null"));

        await harness.StartApiAsync();
        var requests = await harness.MockStatAsync("requests");
        await AgeKeyAsync("k2");
        using var retry = await harness.PostAsync("/v1/payments", "k2", Sale);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var body = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(paymentId, body.GetProperty("id").GetGuid());
        Assert.Equal("CAPTURED", body.GetProperty("state").GetString());
        Assert.Equal(requests, await harness.MockStatAsync("requests"));
        Assert.Equal(1L, await harness.MockStatAsync("effects"));
    }

    [Fact]
    public async Task K3_after_a_restart_a_same_key_retry_of_an_unknown_payment_never_calls_the_psp()
    {
        // The PSP charges and never answers, so the API's deadline records UNKNOWN and answers 202.
        await using var harness = await CrashHarness.StartAsync(db, new { approve_then_hang_rate = 1.0 });
        using (var first = await harness.PostAsync("/v1/payments", "k3", Sale))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        harness.CrashApi();
        await harness.StartApiAsync();
        var requests = await harness.MockStatAsync("requests");

        // E27: an UNKNOWN operation replays its state at once, before any in-flight window.
        using var retry = await harness.PostAsync("/v1/payments", "k3", Sale);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal("UNKNOWN", JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
        Assert.Equal(requests, await harness.MockStatAsync("requests"));
        Assert.Equal(1L, await harness.MockStatAsync("effects"));
        Assert.Equal(1L, await ScalarAsync("select count(*) from payments"));
        Assert.Equal(0L, await UnbalancedAsync());
    }

    private async Task<(Guid Payment, Guid Attempt, string PaymentState, string AttemptState)> SingleAttemptAsync()
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select p.id, a.attempt_id, p.state, a.state from payments p join payment_attempts a on a.payment_id = p.id",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "No payment was recorded.");
        var row = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3));
        Assert.False(await reader.ReadAsync(), "More than one attempt was recorded.");
        return row;
    }

    private Task<long> UnbalancedAsync() => ScalarAsync(
        """
        select count(*) from (
          select txn_id from ledger_entries
          group by txn_id, currency
          having sum(case direction when 'D' then amount_minor else -amount_minor end) <> 0) unbalanced
        """);

    private async Task WaitForAsync(string sql, long atLeast)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await ScalarAsync(sql) >= atLeast)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(sql);
    }

    private Task AgeKeyAsync(string key) =>
        ExecAsync($"update idempotency_keys set created_at = now() - interval '1 minute' where key = '{key}'");

    private static async Task<Guid> IdAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
