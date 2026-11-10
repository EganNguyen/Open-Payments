using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

// Task 25: the event and the transition commit together; a duplicate changes nothing and a
// contradiction against a final state is recorded without moving money.
[Collection("postgres")]
public sealed class CallbackApplyTests(DbFixture db)
{
    [Fact]
    public async Task Callback_applies_in_the_same_transaction_as_the_insert()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var amount = new Money(1000, CurrencyCode.Usd);
        var callback = new PspEvent(
            "evt_apply", PspEventKind.Authorized,
            PspEventReference.ForAttempt(payment.AttemptId), "psp_1", amount, null, null);

        var outcome = await store.Events.ReceiveAsync(
            seed.PspAccountId, callback, Payload("evt_apply"), CancellationToken.None);

        Assert.Equal(EventOutcome.Applied, outcome);
        await using var connection = await db.OpenAdminAsync();
        Assert.Equal("AUTHORIZED", await TextAsync(connection, "select state from payments where id = @id", payment.PaymentId.Value));
        Assert.Equal("SUCCEEDED", await TextAsync(connection, "select state from payment_attempts where attempt_id = @id", payment.AttemptId.Value));
        Assert.Equal("applied", await TextAsync(connection, "select outcome from psp_events where psp_event_id = 'evt_apply'"));

        var duplicate = await store.Events.ReceiveAsync(
            seed.PspAccountId, callback, Payload("evt_apply"), CancellationToken.None);
        Assert.Equal(EventOutcome.Duplicate, duplicate);
        Assert.Equal(1, await CountAsync(connection, "select count(*) from psp_events"));
    }

    [Fact]
    public async Task Declined_callback_against_a_succeeded_attempt_is_a_contradiction()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AUTHORIZED", attemptState: "SUCCEEDED");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var callback = new PspEvent(
            "evt_contradiction", PspEventKind.Declined,
            PspEventReference.ForAttempt(payment.AttemptId), "psp_1", null, "declined", null);

        var outcome = await store.Events.ReceiveAsync(
            seed.PspAccountId, callback, Payload("evt_contradiction"), CancellationToken.None);

        Assert.Equal(EventOutcome.Contradiction, outcome);
        await using var connection = await db.OpenAdminAsync();
        Assert.Equal("AUTHORIZED", await TextAsync(connection, "select state from payments where id = @id", payment.PaymentId.Value));
        Assert.Equal("contradiction", await TextAsync(connection, "select outcome from psp_events where psp_event_id = 'evt_contradiction'"));
    }

    private static string Payload(string eventId) =>
        $$"""{"event_id":"{{eventId}}","type":"authorize.approved","reference":"00000000-0000-0000-0000-000000000000"}""";

    private static async Task<string> TextAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> TextAsync(NpgsqlConnection connection, string sql, object id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
