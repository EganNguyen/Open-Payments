using System.Text.Json;
using Npgsql;
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
