using System.Text.Json;
using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

// Task 20 (D61): the store resolves a key wherever an operation finishes, by primary key, in the
// same transaction. The request path can only override the body it used to store.
[Collection("postgres")]
public sealed class KeyResolutionTests(DbFixture db)
{
    [Fact]
    public async Task Timeout_leaves_key_unresolved()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await SeedKeyAsync(seed, "timeout-key", payment, resolvesKey: true);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var applied = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.Timeout(payment.AttemptId, AttemptOperation.Authorize, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));

        Assert.Equal(PaymentState.Unknown, applied.Payment.State);
        Assert.False(await ResolvedAtAsync("timeout-key"));
    }

    [Fact]
    public async Task Reconciler_finish_resolves_key_with_the_same_body_the_api_renders()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await SeedKeyAsync(seed, "recovery-key", payment, resolvesKey: true);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var amount = new Money(1000, CurrencyCode.Usd);

        // No KeyAnswer: this is the reconciler's path, not the request's.
        var applied = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.RecoveryAnswer(
                payment.AttemptId, AttemptOperation.Authorize, RecoveryOutcome.Approved,
                amount, null, null, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));

        Assert.Equal(PaymentState.Authorized, applied.Payment.State);
        var stored = await StoredAsync("recovery-key");
        Assert.NotNull(stored.ResolvedAt);
        Assert.Equal(200, stored.Code);
        var expected = AnswerRenderer.Render(
            applied.Payment,
            new OperationRef.Attempt(payment.AttemptId, AttemptOperation.Authorize));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse(stored.Body).RootElement,
            JsonDocument.Parse(expected.Body).RootElement));
        Assert.Equal("AUTHORIZED", JsonDocument.Parse(stored.Body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Already_resolved_key_is_not_overwritten()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await SeedKeyAsync(seed, "kept-key", payment, resolvesKey: true, resolvedBody: """{"marker":"kept"}""");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.RecoveryAnswer(
                payment.AttemptId, AttemptOperation.Authorize, RecoveryOutcome.Approved,
                new Money(1000, CurrencyCode.Usd), null, null, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);

        var stored = await StoredAsync("kept-key");
        var body = JsonDocument.Parse(stored.Body).RootElement;
        Assert.Equal("kept", body.GetProperty("marker").GetString());
        Assert.Equal(200, stored.Code);
    }

    [Fact]
    public async Task Key_update_uses_primary_key_not_attempt_scan()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await SeedKeyAsync(seed, "plan-key", payment, resolvesKey: true);
        await using var connection = await db.OpenAdminAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("set local enable_seqscan = off", connection, transaction))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            explain (costs off)
            update idempotency_keys
               set response_code = 200, response_body = '{}'::jsonb, resolved_at = now()
             where merchant_id = @merchant and key = @key and resolved_at is null
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("merchant", seed.Merchant.Value);
        command.Parameters.AddWithValue("key", "plan-key");
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains(plan, line => line.Contains("Index Scan", StringComparison.Ordinal));
        Assert.Contains(plan, line => line.Contains("_pkey", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Seq Scan", StringComparison.Ordinal));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Two_call_authorize_row_does_not_resolve_its_key()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var paymentId = new PaymentId(Guid.NewGuid());
        var hash = System.Security.Cryptography.SHA256.HashData("body"u8);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var begin = Assert.IsType<BeginResult.Started>(await store.BeginAttemptAsync(
            new BeginAttempt(
                seed.Merchant, "two-call-key", hash, paymentId, new Money(1000, CurrencyCode.Usd),
                AttemptOperation.Authorize, CreatePayment: true,
                DateTimeOffset.UtcNow.AddMinutes(10), ResolvesKey: false),
            CancellationToken.None));

        await store.ApplyAsync(
            paymentId,
            new PaymentCommand.PspAnswer(
                begin.AttemptId, AttemptOperation.Authorize, PspAnswerOutcome.Approved,
                new Money(1000, CurrencyCode.Usd), null, null, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);

        Assert.False(await ResolvedAtAsync("two-call-key"));
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select idempotency_key, resolves_key from payment_attempts where payment_id = @p and attempt_id = @a",
            connection);
        command.Parameters.AddWithValue("p", paymentId.Value);
        command.Parameters.AddWithValue("a", begin.AttemptId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("two-call-key", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
    }

    private async Task SeedKeyAsync(
        MerchantSeed seed,
        string key,
        SeededPayment payment,
        bool resolvesKey,
        string? resolvedBody = null)
    {
        await using var connection = await db.OpenAdminAsync();
        await using (var command = new NpgsqlCommand(
            """
            insert into idempotency_keys
              (merchant_id, key, request_hash, payment_id, attempt_id, response_code, response_body, resolved_at)
            values
              (@merchant, @key, '\x01', @payment, @attempt, @code, @body::jsonb, @resolved)
            """,
            connection))
        {
            command.Parameters.AddWithValue("merchant", seed.Merchant.Value);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("payment", payment.PaymentId.Value);
            command.Parameters.AddWithValue("attempt", payment.AttemptId.Value);
            command.Parameters.AddWithValue("code", (object?)(resolvedBody is null ? null : 200) ?? DBNull.Value);
            command.Parameters.AddWithValue("body", (object?)resolvedBody ?? DBNull.Value);
            command.Parameters.AddWithValue("resolved", resolvedBody is null ? DBNull.Value : DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        await using var link = new NpgsqlCommand(
            "update payment_attempts set idempotency_key = @key, resolves_key = @resolves where payment_id = @p and attempt_id = @a",
            connection);
        link.Parameters.AddWithValue("key", key);
        link.Parameters.AddWithValue("resolves", resolvesKey);
        link.Parameters.AddWithValue("p", payment.PaymentId.Value);
        link.Parameters.AddWithValue("a", payment.AttemptId.Value);
        await link.ExecuteNonQueryAsync();
    }

    private async Task<bool> ResolvedAtAsync(string key)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select resolved_at is not null from idempotency_keys where key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(DateTimeOffset? ResolvedAt, int? Code, string Body)> StoredAsync(string key)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select resolved_at, response_code, response_body::text from idempotency_keys where key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetDateTime(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
    }
}
