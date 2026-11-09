using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

[Collection("postgres")]
public sealed class PaymentStoreTests(DbFixture db)
{
    [Fact]
    public async Task Apply_commits_state_ledger_outbox_or_nothing()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AUTHORIZED");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var amount = new Money(1000, CurrencyCode.Usd);

        var capture = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.Capture(new AttemptId(Guid.NewGuid()), amount, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));
        var captureAttempt = capture.Payment.Attempt!.Id;

        store.BeforeOutboxInsert = () => throw new InvalidOperationException("failpoint");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.CaptureAnswer(captureAttempt, true, amount, null, false, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));

        await using (var connection = await db.OpenAdminAsync())
        {
            Assert.Equal("AUTHORIZED", await TextAsync(connection, "select state from payments where id = @id", payment.PaymentId));
            Assert.Equal(0, await CountAsync(connection, "select count(*) from ledger_transactions where payment_id = @id", payment.PaymentId));
            Assert.Equal(0, await CountAsync(connection, "select count(*) from outbox where aggregate_id = @id", payment.PaymentId));
        }

        store.BeforeOutboxInsert = null;
        var applied = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.CaptureAnswer(captureAttempt, true, amount, null, false, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));

        Assert.Equal(PaymentState.Captured, applied.Payment.State);
        await using (var connection = await db.OpenAdminAsync())
        {
            Assert.Equal(2, await CountAsync(connection, "select count(*) from ledger_transactions where payment_id = @id", payment.PaymentId));
            Assert.Equal(4, await CountAsync(connection, "select count(*) from ledger_entries where subject_id = @id", payment.PaymentId));
            Assert.Equal(1, await CountAsync(connection, "select count(*) from outbox where aggregate_id = @id", payment.PaymentId));
        }
    }

    [Fact]
    public async Task Zero_rows_update_returns_already_moved_never_overwrites()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AUTHORIZED");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        store.BeforePaymentUpdate = () =>
        {
            using var connection = db.OpenAdminAsync().GetAwaiter().GetResult();
            using var command = new NpgsqlCommand(
                "update payments set state = 'VOIDED', version = version + 1 where id = @id",
                connection);
            command.Parameters.AddWithValue("id", payment.PaymentId.Value);
            command.ExecuteNonQuery();
        };

        var result = await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.Expiry(DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);

        Assert.IsType<ApplyResult.AlreadyMoved>(result);
        await using (var connection = await db.OpenAdminAsync())
        {
            Assert.Equal("VOIDED", await TextAsync(connection, "select state from payments where id = @id", payment.PaymentId));
        }
    }

    [Fact]
    public async Task Begin_duplicate_key_same_hash_replays_stored()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AWAITING_METHOD", attemptState: "FAILED");
        await SeedKeyAsync(seed.Merchant, "same", "body", payment.PaymentId, payment.AttemptId, 201, "{\"id\":\"p_1\"}");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var result = await store.BeginAttemptAsync(
            Command(seed, payment.PaymentId, "same", "body"),
            CancellationToken.None);

        var replay = Assert.IsType<BeginResult.Replay>(result);
        Assert.Equal(201, replay.Response.StatusCode);
        Assert.Equal("{\"id\": \"p_1\"}", replay.Response.Body);
    }

    [Fact]
    public async Task Begin_duplicate_key_different_hash_mismatches()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AWAITING_METHOD", attemptState: "FAILED");
        await SeedKeyAsync(seed.Merchant, "same", "body-a", payment.PaymentId, payment.AttemptId, 201, "{}");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var result = await store.BeginAttemptAsync(
            Command(seed, payment.PaymentId, "same", "body-b"),
            CancellationToken.None);

        Assert.IsType<BeginResult.HashMismatch>(result);
    }

    [Fact]
    public async Task Begin_while_unresolved_reports_in_flight()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "PSP_PENDING", attemptState: "PENDING");
        await SeedKeyAsync(seed.Merchant, "same", "body", payment.PaymentId, payment.AttemptId, null, null);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        // E2: a fresh unresolved key may belong to a running request.
        var running = await store.BeginAttemptAsync(
            Command(seed, payment.PaymentId, "same", "body"),
            CancellationToken.None);
        Assert.IsType<BeginResult.KeyInFlight>(running);

        // D31, review M4: past the in-flight window the request has ended without its answer, so
        // a replay renders the current state instead of 409 forever.
        await db.ExecAdminAsync("update idempotency_keys set created_at = now() - interval '1 minute' where key = 'same'");
        var ended = await store.BeginAttemptAsync(
            Command(seed, payment.PaymentId, "same", "body"),
            CancellationToken.None);
        var pending = Assert.IsType<BeginResult.Pending>(ended);
        Assert.Equal(payment.PaymentId, pending.Payment.Id);
    }

    [Fact]
    public async Task One_live_authorize_index_blocks_second_attempt()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var paymentId = new PaymentId(Guid.NewGuid());
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        Assert.IsType<BeginResult.Started>(await store.BeginAttemptAsync(
            Command(seed, paymentId, "first", "first"),
            CancellationToken.None));
        var second = await store.BeginAttemptAsync(
            Command(seed, paymentId, "second", "second"),
            CancellationToken.None);

        Assert.IsType<BeginResult.LiveAttemptExists>(second);
        await using var connection = await db.OpenAdminAsync();
        Assert.Equal(1, await CountAsync(connection, "select count(*) from payment_attempts where payment_id = @id", paymentId));
    }

    [Fact]
    public async Task Ledger_role_cannot_update_or_delete()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var transactionId = Guid.NewGuid();
        await using (var connection = await db.OpenAdminAsync())
        {
            await using (var txn = new NpgsqlCommand(
                """
                insert into ledger_transactions (id, merchant_id, payment_id, kind, source_event_id, effective_at)
                values (@id, @merchant, null, 'test', @id, now())
                """,
                connection))
            {
                txn.Parameters.AddWithValue("id", transactionId);
                txn.Parameters.AddWithValue("merchant", seed.Merchant.Value);
                await txn.ExecuteNonQueryAsync();
            }

            var lines = new[] { ("auth_hold", 'D'), ("auth_hold_contra", 'C') };
            await using var ledger = await connection.BeginTransactionAsync();
            for (var index = 0; index < lines.Length; index++)
            {
                var (accountType, direction) = lines[index];
                await using var entry = new NpgsqlCommand(
                    """
                    insert into ledger_entries
                      (txn_id, entry_no, merchant_id, account_id, subject_id, direction, amount_minor, currency)
                    select @txn, @entry, @merchant, id, @subject, @direction, 100, 'USD'
                    from accounts where merchant_id = @merchant and account_type = @type and sub_account = 0
                    """,
                    connection,
                    ledger);
                entry.Parameters.AddWithValue("txn", transactionId);
                entry.Parameters.AddWithValue("entry", (short)(index + 1));
                entry.Parameters.AddWithValue("merchant", seed.Merchant.Value);
                entry.Parameters.AddWithValue("subject", Guid.NewGuid());
                entry.Parameters.AddWithValue("direction", direction.ToString());
                entry.Parameters.AddWithValue("type", accountType);
                await entry.ExecuteNonQueryAsync();
            }
            await ledger.CommitAsync();
        }

        await using var app = await db.OpenAppAsync();
        var update = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "update ledger_entries set amount_minor = 1 where txn_id = @txn",
                app);
            command.Parameters.AddWithValue("txn", transactionId);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "delete from ledger_entries where txn_id = @txn",
                app);
            command.Parameters.AddWithValue("txn", transactionId);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", delete.SqlState);
    }

    [Fact]
    public async Task Resolved_at_set_in_same_transaction_as_final_state()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "AUTHORIZED");
        var amount = new Money(1000, CurrencyCode.Usd);
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);
        var capture = Assert.IsType<ApplyResult.Applied>(await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.Capture(new AttemptId(Guid.NewGuid()), amount, DateTimeOffset.UtcNow),
            null,
            CancellationToken.None));
        var captureAttempt = capture.Payment.Attempt!.Id;
        await SeedKeyAsync(seed.Merchant, "capture-key", "capture", payment.PaymentId, captureAttempt, null, null);

        await store.ApplyAsync(
            payment.PaymentId,
            new PaymentCommand.CaptureAnswer(captureAttempt, true, amount, null, false, DateTimeOffset.UtcNow),
            new StoredResponse(200, "{\"status\":\"captured\"}"),
            CancellationToken.None);

        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            select p.state, k.resolved_at is not null
            from payments p
            join idempotency_keys k on k.payment_id = p.id
            where p.id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", payment.PaymentId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CAPTURED", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Key_conflict_does_not_abort_the_transaction()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var paymentId = new PaymentId(Guid.NewGuid());
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var results = await Task.WhenAll(
            store.BeginAttemptAsync(Command(seed, paymentId, "race", "race"), CancellationToken.None),
            store.BeginAttemptAsync(Command(seed, paymentId, "race", "race"), CancellationToken.None));

        Assert.Contains(results, result => result is BeginResult.Started);
        Assert.Contains(results, result => result is BeginResult.KeyInFlight or BeginResult.Replay);
        await using var connection = await db.OpenAdminAsync();
        Assert.Equal(1, await CountAsync(connection, "select count(*) from payments where id = @id", paymentId));
    }

    [Fact]
    public async Task E29_refusal_stores_no_key()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "REFUNDED");
        await using var runner = new TxRunner(db.AppConnectionString);
        var store = new PaymentStore(runner);

        var result = await store.BeginAttemptAsync(
            Command(seed, payment.PaymentId, "refused", "refused", createPayment: false),
            CancellationToken.None);

        Assert.IsType<BeginResult.Rejected>(result);
        Assert.IsType<KeyLookup.NotFound>(await store.ReadKeyAsync(
            seed.Merchant, "refused", Hash("refused"), CancellationToken.None));
    }

    private static BeginAttempt Command(MerchantSeed seed, PaymentId paymentId, string key, string body, bool createPayment = true) =>
        new(
            seed.Merchant,
            key,
            Hash(body),
            paymentId,
            new Money(1000, CurrencyCode.Usd),
            AttemptOperation.Authorize,
            CreatePayment: createPayment,
            DateTimeOffset.UtcNow.AddMinutes(30),
            PspAccountId: seed.PspAccountId);

    private async Task SeedKeyAsync(
        MerchantId merchant,
        string key,
        string body,
        PaymentId payment,
        AttemptId attempt,
        int? responseCode,
        string? responseBody)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into idempotency_keys
              (merchant_id, key, request_hash, payment_id, attempt_id,
               response_code, response_body, resolved_at)
            values
              (@merchant, @key, @hash, @payment, @attempt, @code,
               @body::jsonb, @resolved)
            """,
            connection);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("hash", Hash(body));
        command.Parameters.AddWithValue("payment", payment.Value);
        command.Parameters.AddWithValue("attempt", attempt.Value);
        command.Parameters.AddWithValue("code", (object?)responseCode ?? DBNull.Value);
        command.Parameters.AddWithValue("body", (object?)responseBody ?? DBNull.Value);
        command.Parameters.AddWithValue("resolved", responseCode is null ? DBNull.Value : DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static byte[] Hash(string body) => SHA256.HashData(Encoding.UTF8.GetBytes(body));

    private static async Task<string> TextAsync(
        NpgsqlConnection connection,
        string sql,
        PaymentId paymentId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", paymentId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection,
        string sql,
        PaymentId paymentId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", paymentId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
