using System.Net;
using System.Text.Json;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task 35: the payment timeline shows PSP events and the reconciler's last inquiry.
[Collection("console-db")]
public sealed class TimelinePspTests(ConsoleDb db)
{
    [Fact]
    public async Task Timeline_shows_each_psp_event_with_source_and_outcome_oldest_first()
    {
        await db.ResetAsync();
        var payment = await db.SeedPaymentAsync("replica", "CAPTURED");
        await db.SeedAttemptAsync("replica", payment, "sale", "UNKNOWN", DateTimeOffset.UtcNow.AddMinutes(-4));
        await db.ExecAsync("replica", $"""
            update payment_attempts set last_inquiry_at = now() - interval '2 minutes',
                                       last_inquiry_result = 'not_found',
                                       recovery_attempts = 3
             where payment_id = '{payment}';
            insert into psp_events (psp_account_id, psp_event_id, source, payload, payment_id, outcome, received_at)
            values ('{ConsoleDb.PspAccount}', 'evt_1', 'callback', jsonb_build_object('type','capture.approved'),
                    '{payment}', 'applied', now() - interval '3 minutes'),
                   ('{ConsoleDb.PspAccount}', 'evt_2', 'api_answer', jsonb_build_object('type','void.approved'),
                    '{payment}', 'contradiction', now() - interval '1 minute');
            """);
        var detail = await ReadAsync(payment);

        var events = detail.Timeline.Where(entry => entry.Kind == TimelineKind.PspEvent).ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains("evt_1", events[0].Text, StringComparison.Ordinal);
        Assert.Contains("callback", events[0].Text, StringComparison.Ordinal);
        Assert.Contains("evt_2", events[1].Text, StringComparison.Ordinal);
        Assert.Contains("contradicted a final state", events[1].Text, StringComparison.Ordinal);
        Assert.True(events[0].At < events[1].At);
    }

    [Fact]
    public async Task Unresolved_row_shows_last_inquiry_and_attempt_count()
    {
        await db.ResetAsync();
        var payment = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await db.SeedAttemptAsync("replica", payment, "authorize", "UNKNOWN", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.ExecAsync("replica", $"""
            update payment_attempts set last_inquiry_at = now() - interval '1 minute',
                                       last_inquiry_result = 'inconclusive',
                                       recovery_attempts = 4
             where payment_id = '{payment}';
            """);
        var detail = await ReadAsync(payment);

        var inquiry = Assert.Single(detail.Timeline, entry => entry.Kind == TimelineKind.Inquiry);
        Assert.Contains("inconclusive", inquiry.Text, StringComparison.Ordinal);
        Assert.Contains("4 recovery attempt(s)", inquiry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payload_text_renders_as_text_never_html()
    {
        await db.ResetAsync();
        var payment = await db.SeedPaymentAsync("replica", "CAPTURED");
        await db.SeedAttemptAsync("replica", payment, "sale", "SUCCEEDED", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.ExecAsync("replica", $"""
            insert into psp_events (psp_account_id, psp_event_id, source, payload, payment_id, outcome, received_at)
            values ('{ConsoleDb.PspAccount}', 'evt_html', 'callback',
                    jsonb_build_object('note','<script>alert(1)</script>'), '{payment}', 'ignored', now() - interval '1 minute');
            """);
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync($"/Payments/{payment}");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_query_uses_psp_events_payment_index()
    {
        await db.ResetAsync();
        await using var connection = new NpgsqlConnection(db.AdminFor("replica"));
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "explain (format json) " + TimelineReader.PspEventsSql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0]
            .GetProperty("Plan");

        Assert.Contains(
            Walk(plan),
            name => name.Contains("psp_events", StringComparison.Ordinal)
                && name.Contains("payment", StringComparison.Ordinal));
    }

    private async Task<PaymentDetail> ReadAsync(Guid payment)
    {
        await using var replica = NpgsqlDataSource.Create(db.As(ConsoleDb.ReadUser, "replica"));
        await using var primary = NpgsqlDataSource.Create(db.As(ConsoleDb.ReadUser, "primary"));
        var reader = new TimelineReader(replica, primary);
        return await reader.ReadAsync(payment, "USD", ReadFrom.Replica, CancellationToken.None);
    }

    private static IEnumerable<string> Walk(JsonElement plan)
    {
        if (plan.TryGetProperty("Index Name", out var index))
        {
            yield return index.GetString()!;
        }

        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var name in Walk(child))
                {
                    yield return name;
                }
            }
        }
    }
}
