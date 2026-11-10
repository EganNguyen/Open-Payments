using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task 21 (L7, D59): a PSP answer is never dropped. A lost conditional update is reapplied once;
// an answer that still can't apply is parked and the client gets 202, never 409 or 422.
[Collection("acceptance")]
public sealed class L7AnswerKeptTests(AcceptanceFixture db)
{
    [Fact]
    public async Task L7_lost_conditional_update_is_reapplied_once_from_a_fresh_snapshot()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await AuthorizedPaymentAsync(client, "l7-lost-auth");

        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var calls = 0;
        store.BeforePaymentUpdate = () =>
        {
            // Transaction A inserts the capture attempt; only transaction B (the answer) sees it.
            if (!CapturePending(paymentId) || Interlocked.Increment(ref calls) != 1)
            {
                return;
            }

            using var connection = new NpgsqlConnection(db.AdminConnectionString);
            connection.Open();
            using var bump = new NpgsqlCommand(
                "update payments set version = version + 1 where id = @id", connection);
            bump.Parameters.AddWithValue("id", Guid.Parse(paymentId));
            bump.ExecuteNonQuery();
        };

        try
        {
            using var captured = await PostAsync(
                client, $"/v1/payments/{paymentId}/capture", "l7-lost-capture", new { amount_minor = 1000 });
            Assert.Equal(HttpStatusCode.OK, captured.StatusCode);
        }
        finally
        {
            store.BeforePaymentUpdate = null;
        }

        // A capture posts two ledger transactions (release the hold, then the capture). A retry
        // must not add a third.
        Assert.Equal(2, await CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{paymentId}' and kind = 'capture'"));
        Assert.Equal(1, await CountAsync(
            $"select count(*) from payment_attempts where payment_id = '{paymentId}' and operation = 'capture' and state = 'SUCCEEDED'"));
    }

    [Fact]
    public async Task L7_answer_refused_twice_is_parked_and_client_gets_202()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await AuthorizedPaymentAsync(client, "l7-park-auth");

        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforePaymentUpdate = () =>
        {
            if (!CapturePending(paymentId))
            {
                return;
            }

            using var connection = new NpgsqlConnection(db.AdminConnectionString);
            connection.Open();
            using var voided = new NpgsqlCommand(
                "update payments set state = 'VOIDED', version = version + 1 where id = @id", connection);
            voided.Parameters.AddWithValue("id", Guid.Parse(paymentId));
            voided.ExecuteNonQuery();
        };

        HttpResponseMessage captured;
        try
        {
            captured = await PostAsync(
                client, $"/v1/payments/{paymentId}/capture", "l7-park-capture", new { amount_minor = 1000 });
        }
        finally
        {
            store.BeforePaymentUpdate = null;
        }

        using (captured)
        {
            Assert.Equal(HttpStatusCode.Accepted, captured.StatusCode);
            var body = JsonDocument.Parse(await captured.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("VOIDED", body.GetProperty("state").GetString());
        }

        Assert.Equal(1, await CountAsync(
            "select count(*) from psp_events where source = 'api_answer' and outcome = 'parked' and attempt_id is not null"));
        Assert.Equal(0, await CountAsync(
            $"select count(*) from ledger_transactions where payment_id = '{paymentId}' and kind = 'capture'"));
    }

    [Fact]
    public async Task L7_park_failure_still_answers_202_and_leaves_attempt_unresolved()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await AuthorizedPaymentAsync(client, "l7-fail-auth");
        await ExecAdminAsync("alter table psp_events rename to psp_events_unavailable");

        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforePaymentUpdate = () =>
        {
            if (!CapturePending(paymentId))
            {
                return;
            }

            using var connection = new NpgsqlConnection(db.AdminConnectionString);
            connection.Open();
            using var voided = new NpgsqlCommand(
                "update payments set state = 'VOIDED', version = version + 1 where id = @id", connection);
            voided.Parameters.AddWithValue("id", Guid.Parse(paymentId));
            voided.ExecuteNonQuery();
        };

        HttpResponseMessage captured;
        try
        {
            captured = await PostAsync(
                client, $"/v1/payments/{paymentId}/capture", "l7-fail-capture", new { amount_minor = 1000 });
        }
        finally
        {
            store.BeforePaymentUpdate = null;
        }

        using (captured)
        {
            Assert.Equal(HttpStatusCode.Accepted, captured.StatusCode);
        }

        Assert.Equal(1, await CountAsync(
            $"select count(*) from payment_attempts where payment_id = '{paymentId}' and operation = 'capture' and state = 'PENDING'"));
        Assert.Equal(1, await CountAsync(
            $"select count(*) from idempotency_keys where key = 'l7-fail-capture' and resolved_at is null"));
    }

    private async Task<string> AuthorizedPaymentAsync(HttpClient client, string key)
    {
        using var created = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_l7" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private bool CapturePending(string paymentId)
    {
        using var connection = new NpgsqlConnection(db.AdminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "select exists(select 1 from payment_attempts where payment_id = @id and operation = 'capture' and state = 'PENDING')",
            connection);
        command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
        return (bool)command.ExecuteScalar()!;
    }

    private async Task ExecAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
