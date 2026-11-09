using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

[Collection("postgres")]
public sealed class TxRunnerTests(DbFixture db)
{
    [Fact]
    public async Task Retries_40P01_at_most_twice_then_throws_retryable()
    {
        await db.ResetAsync();
        var merchant = (await db.SeedMerchantAsync()).Merchant;
        await using var runner = new TxRunner(db.AppConnectionString);
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<BusyException>(() => runner.RunAsync(
            merchant,
            async (connection, transaction, cancellationToken) =>
            {
                attempts++;
                await using var command = new NpgsqlCommand(
                    "do $$ begin raise exception 'deadlock' using errcode = '40P01'; end $$;",
                    connection,
                    transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            CancellationToken.None));

        Assert.Equal("retry_exhausted", exception.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task D54_runner_uses_the_role_lock_timeout()
    {
        await db.ResetAsync();
        var merchant = (await db.SeedMerchantAsync()).Merchant;
        // The fixture gives op_app D1's role setting, as scripts/check-db-settings.sh checks on the stack.
        {
            await using var runner = new TxRunner(db.AppConnectionString);
            var inside = await runner.RunAsync(
                merchant,
                async (connection, transaction, cancellationToken) =>
                {
                    await using var command = new NpgsqlCommand("select current_setting('lock_timeout')", connection, transaction);
                    return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
                },
                CancellationToken.None);

            Assert.Equal("200ms", inside);
        }
    }

    [Fact]
    public async Task Real_deadlock_under_D1_settings_is_detected_and_retried()
    {
        // Review M6: the fixture runs D1's pair, deadlock_timeout 100 ms under lock_timeout 200 ms,
        // so opposite lock orders deadlock (40P01) and are retried, instead of timing out (55P03).
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var first = await db.SeedPaymentAsync(seed, "AUTHORIZED");
        var second = await db.SeedPaymentAsync(seed, "AUTHORIZED");
        await using var runner = new TxRunner(db.AppConnectionString);
        using var bothHoldOne = new Barrier(2);
        var arrivals = 0;

        Task<int> Touch(PaymentId a, PaymentId b) => runner.RunAsync(
            seed.Merchant,
            async (connection, transaction, cancellationToken) =>
            {
                await Update(connection, transaction, a, cancellationToken);
                // Only the two first attempts meet at the barrier; the retry runs alone.
                if (Interlocked.Increment(ref arrivals) <= 2)
                {
                    bothHoldOne.SignalAndWait(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                await Update(connection, transaction, b, cancellationToken);
                return 0;
            },
            CancellationToken.None);

        await Task.WhenAll(
            Task.Run(() => Touch(first.PaymentId, second.PaymentId)),
            Task.Run(() => Touch(second.PaymentId, first.PaymentId)));

        Assert.True(runner.Metrics.Retries.GetValueOrDefault("40P01") >= 1, "No 40P01 was retried.");
        Assert.Equal(2L, await db.ScalarAsync(
            $"select count(*) from payments where id in ('{first.PaymentId.Value}', '{second.PaymentId.Value}') and version = 2"));

        static async Task Update(NpgsqlConnection connection, NpgsqlTransaction transaction, PaymentId id, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("update payments set version = version + 1 where id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", id.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task Does_not_retry_55P03()
    {
        await db.ResetAsync();
        var merchant = (await db.SeedMerchantAsync()).Merchant;
        await using var runner = new TxRunner(db.AppConnectionString);
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<BusyException>(() => runner.RunAsync(
            merchant,
            async (connection, transaction, cancellationToken) =>
            {
                attempts++;
                await using var command = new NpgsqlCommand(
                    "do $$ begin raise exception 'lock' using errcode = '55P03'; end $$;",
                    connection,
                    transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            CancellationToken.None));

        Assert.Equal("lock_timeout", exception.Reason);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Does_not_retry_unique_violation()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        await using (var connection = await db.OpenAdminAsync())
        await using (var insert = new NpgsqlCommand(
            "insert into idempotency_keys (merchant_id, key, request_hash) values (@merchant, 'dup', '\\x00'::bytea)",
            connection))
        {
            insert.Parameters.AddWithValue("merchant", seed.Merchant.Value);
            await insert.ExecuteNonQueryAsync();
        }

        await using var runner = new TxRunner(db.AppConnectionString);
        var attempts = 0;
        await Assert.ThrowsAsync<PostgresException>(() => runner.RunAsync(
            seed.Merchant,
            async (connection, transaction, cancellationToken) =>
            {
                attempts++;
                await using var command = new NpgsqlCommand(
                    "insert into idempotency_keys (merchant_id, key, request_hash) values (@merchant, 'dup', '\\x00'::bytea)",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("merchant", seed.Merchant.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Sets_local_merchant_id_each_transaction()
    {
        await db.ResetAsync();
        var first = (await db.SeedMerchantAsync()).Merchant;
        var second = (await db.SeedMerchantAsync()).Merchant;
        var builder = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 };

        await using var runner = new TxRunner(builder.ConnectionString);
        Assert.Equal(first, await ReadMerchantAsync(runner, first));
        Assert.Equal(second, await ReadMerchantAsync(runner, second));
        Assert.Equal(first, await ReadMerchantAsync(runner, first));
    }

    private static async Task<MerchantId> ReadMerchantAsync(TxRunner runner, MerchantId merchant) =>
        await runner.RunAsync(
            merchant,
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = new NpgsqlCommand(
                    "select current_setting('app.merchant_id')",
                    connection,
                    transaction);
                var value = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
                return new MerchantId(Guid.Parse(value));
            },
            CancellationToken.None);
}
