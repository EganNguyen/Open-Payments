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

// Task 22 (L8): once the authorization has been sent, a failed first read or capture start on the
// two-call path answers 202 with the payment id, never 5xx or 409.
[Collection("acceptance")]
public sealed class L8TwoCallCaptureTests(AcceptanceFixture db)
{
    [Fact]
    public async Task L8_database_failure_on_capture_first_read_after_authorization_answers_202()
    {
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        using var client = Authorized();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var thrown = 0;
        store.BeforeRead = () =>
        {
            if (thrown == 0
                && db.Psp.Calls.Any(call => call.Operation == "authorize")
                && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new NpgsqlException("scripted first read failure");
            }
        };

        HttpResponseMessage created;
        try
        {
            created = await PostAsync(client, "l8-read", new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_l8" },
                capture_mode = "auto",
            });
        }
        finally
        {
            store.BeforeRead = null;
        }

        using (created)
        {
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("id").GetString()));
            Assert.Equal("AUTHORIZED", body.GetProperty("state").GetString());
        }

        Assert.Single(db.Psp.Calls, call => call.Operation == "authorize");
        Assert.DoesNotContain(db.Psp.Calls, call => call.Operation == "capture");
    }

    [Fact]
    public async Task L8_same_key_retry_after_window_captures_once()
    {
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        using var client = Authorized();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var thrown = 0;
        store.BeforeRead = () =>
        {
            if (thrown == 0
                && db.Psp.Calls.Any(call => call.Operation == "authorize")
                && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new NpgsqlException("scripted first read failure");
            }
        };

        using var created = await PostAsync(client, "l8-retry", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_l8_retry" },
            capture_mode = "auto",
        });
        store.BeforeRead = null;
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var paymentId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        // E2: inside the in-flight window a retry is 409 by design. The test's "after window"
        // case ages the key past `PaymentStore.InFlightWindow`.
        await ExecAdminAsync(
            "update idempotency_keys set created_at = now() - interval '31 seconds' where key = 'l8-retry'");

        using var retried = await PostAsync(client, "l8-retry", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_l8_retry" },
            capture_mode = "auto",
        });

        var retriedBody = await retried.Content.ReadAsStringAsync();
        Assert.True(retried.StatusCode == HttpStatusCode.OK, retriedBody);
        Assert.Equal(1, db.Psp.Calls.Count(call => call.Operation == "capture"));
        Assert.Equal(1, await CountAsync(
            $"select count(*) from payment_attempts where payment_id = '{paymentId}' and operation = 'capture' and state = 'SUCCEEDED'"));
        Assert.Equal(1, await CountAsync(
            $"select count(*) from idempotency_keys where key = 'l8-retry' and resolved_at is not null"));
    }

    private async Task ExecAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
