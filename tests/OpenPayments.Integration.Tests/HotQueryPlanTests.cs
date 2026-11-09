using System.Text.Json;
using Npgsql;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

[Collection("postgres")]
public sealed class HotQueryPlanTests(DbFixture db)
{
    [Fact]
    public async Task S3_hot_queries_use_expected_indexes()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        await db.SeedPaymentAsync(seed, "AUTHORIZED");

        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            explain (format json)
            select id, state, amount_minor
            from payments
            where merchant_id = @merchant
            order by created_at desc
            limit 50
            """,
            connection);
        command.Parameters.AddWithValue("merchant", seed.Merchant.Value);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement[0].GetProperty("Plan");
        var index = FindIndexName(plan);
        Assert.NotNull(index);
        Assert.Contains("merchant_id_created_at", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D50_load_refund_uses_refunds_payment()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var payment = await db.SeedPaymentAsync(seed, "CAPTURED");

        await using var connection = await db.OpenAdminAsync();
        await using (var insert = new NpgsqlCommand(
            """
            insert into refunds (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id)
            select gen_random_uuid(), @payment, @merchant, 100, 'SUCCEEDED', 'mock', @psp_account
            from generate_series(1, 2)
            """,
            connection))
        {
            insert.Parameters.AddWithValue("payment", payment.PaymentId.Value);
            insert.Parameters.AddWithValue("merchant", seed.Merchant.Value);
            insert.Parameters.AddWithValue("psp_account", seed.PspAccountId);
            await insert.ExecuteNonQueryAsync();
        }

        // On a two-row table a sequential scan is the right plan. Disabling it shows whether an
        // index matches the query, not what the planner picks at volume.
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("explain (format json) " + Sql.LoadRefund, connection);
        command.Parameters.AddWithValue("payment_id", payment.PaymentId.Value);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement[0].GetProperty("Plan");
        Assert.Equal("refunds_payment", FindIndexName(plan));
    }

    private static string? FindIndexName(JsonElement plan)
    {
        if (plan.TryGetProperty("Index Name", out var name))
        {
            return name.GetString();
        }

        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var found = FindIndexName(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
