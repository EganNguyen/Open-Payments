using Npgsql;
using OpenPayments.Console.Presentation;

namespace OpenPayments.Console.Data;

public enum TimelineKind
{
    Attempt,
    Refund,
    Ledger,
    Staff,
    PspEvent,
    Inquiry,
}

public sealed record TimelineEntry(DateTimeOffset At, TimelineKind Kind, string Text);

public sealed record AttemptRow(
    Guid Id,
    string Operation,
    string State,
    string? PspReference,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastInquiryAt = null,
    string? LastInquiryResult = null,
    int RecoveryAttempts = 0);

public sealed record RefundRow(
    Guid Id,
    long AmountMinor,
    string State,
    string? PspReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastInquiryAt = null,
    string? LastInquiryResult = null,
    int RecoveryAttempts = 0)
{
    public bool Unresolved => State is "PENDING" or "UNKNOWN" or "REVIEW";
}

public sealed record PaymentDetail(
    IReadOnlyList<AttemptRow> Attempts,
    IReadOnlyList<RefundRow> Refunds,
    IReadOnlyList<TimelineEntry> Timeline)
{
    /// <summary>The PSP reference of the attempt that took or reserved the money, if any.</summary>
    public string? PspReference =>
        Attempts.Where(a => a.Operation is "authorize" or "sale" && a.State == "SUCCEEDED").Select(a => a.PspReference).FirstOrDefault()
        ?? Attempts.Select(a => a.PspReference).FirstOrDefault(reference => reference is not null);
}

public interface ITimelineReader
{
    Task<PaymentDetail> ReadAsync(Guid paymentId, string currency, ReadFrom from, CancellationToken ct);
}

/// <summary>
/// Version 1's timeline: when attempts and refunds began, ledger entries and staff actions.
/// PSP events, webhook deliveries and inquiry results join when later stages store them.
/// Text from outside, such as a failure code, is kept as data; pages encode it.
/// </summary>
public sealed class TimelineReader(NpgsqlDataSource replica, NpgsqlDataSource primary) : ITimelineReader
{
    public async Task<PaymentDetail> ReadAsync(Guid paymentId, string currency, ReadFrom from, CancellationToken ct)
    {
        var source = from == ReadFrom.Replica ? replica : primary;
        await using var connection = await source.OpenConnectionAsync(ct);

        var attempts = new List<AttemptRow>();
        await using (var command = new NpgsqlCommand(
            "select attempt_id, operation, state, psp_reference, failure_code, created_at, last_inquiry_at, last_inquiry_result, recovery_attempts from payment_attempts where payment_id = @id order by created_at, attempt_id",
            connection))
        {
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                attempts.Add(new AttemptRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8)));
            }
        }

        var refunds = new List<RefundRow>();
        await using (var command = new NpgsqlCommand(
            "select id, amount_minor, state, psp_reference, created_at, last_inquiry_at, last_inquiry_result, recovery_attempts from refunds where payment_id = @id order by created_at, id",
            connection))
        {
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                refunds.Add(new RefundRow(
                    reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7)));
            }
        }

        var timeline = new List<TimelineEntry>();
        foreach (var attempt in attempts)
        {
            var text = $"{Capitalize(attempt.Operation)} attempt, now {attempt.State.ToLowerInvariant()}";
            if (attempt.FailureCode is not null)
            {
                text += ", failure code " + attempt.FailureCode;
            }

            timeline.Add(new TimelineEntry(attempt.CreatedAt, TimelineKind.Attempt, text));
        }

        foreach (var refund in refunds)
        {
            var status = refund.Unresolved ? "in progress, amount reserved" : refund.State.ToLowerInvariant();
            timeline.Add(new TimelineEntry(refund.CreatedAt, TimelineKind.Refund,
                $"Refund of {MoneyFormat.DisplayWithCode(refund.AmountMinor, currency)}, {status}"));
        }

        await using (var command = new NpgsqlCommand(
            """
            select t.created_at, t.kind, coalesce(sum(e.amount_minor) filter (where e.direction = 'D'), 0)
            from ledger_transactions t
            left join ledger_entries e on e.txn_id = t.id
            where t.payment_id = @id
            group by t.id, t.created_at, t.kind
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                timeline.Add(new TimelineEntry(reader.GetFieldValue<DateTimeOffset>(0), TimelineKind.Ledger,
                    $"Ledger: {reader.GetString(1)}, {MoneyFormat.DisplayWithCode(reader.GetInt64(2), currency)} balanced"));
            }
        }

        await using (var command = new NpgsqlCommand(
            """
            select a.created_at, a.action, a.phase, a.result, a.reason, s.email
            from console.audit_log a left join console.staff s on s.id = a.staff_id
            where a.payment_id = @id and a.phase in ('outcome', 'event')
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var who = reader.IsDBNull(5) ? "A staff member" : reader.GetString(5);
                var result = reader.IsDBNull(3) ? string.Empty : ": " + reader.GetString(3);
                var reason = reader.IsDBNull(4) ? string.Empty : " (reason: " + reader.GetString(4) + ")";
                timeline.Add(new TimelineEntry(reader.GetFieldValue<DateTimeOffset>(0), TimelineKind.Staff,
                    $"{who} {reader.GetString(1)}{result}{reason}"));
            }
        }

        await using (var command = new NpgsqlCommand(PspEventsSql, connection))
        {
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var eventSource = reader.GetString(1);
                var outcome = reader.IsDBNull(3) ? "received" : reader.GetString(3);
                var payload = reader.GetString(4);
                var text = outcome switch
                {
                    "contradiction" => $"PSP event {reader.GetString(2)} from {eventSource} contradicted a final state: {payload}",
                    "parked" => $"PSP event {reader.GetString(2)} from {eventSource} is parked: {payload}",
                    _ => $"PSP event {reader.GetString(2)} from {eventSource}, {outcome}: {payload}",
                };
                timeline.Add(new TimelineEntry(
                    reader.GetFieldValue<DateTimeOffset>(0), TimelineKind.PspEvent, text));
            }
        }

        foreach (var attempt in attempts.Where(a => a.LastInquiryAt is not null))
        {
            timeline.Add(new TimelineEntry(
                attempt.LastInquiryAt!.Value,
                TimelineKind.Inquiry,
                $"Last inquiry for the {attempt.Operation} attempt: {attempt.LastInquiryResult ?? "no result"}, {attempt.RecoveryAttempts} recovery attempt(s)"));
        }

        foreach (var refund in refunds.Where(r => r.LastInquiryAt is not null))
        {
            timeline.Add(new TimelineEntry(
                refund.LastInquiryAt!.Value,
                TimelineKind.Inquiry,
                $"Last inquiry for the refund: {refund.LastInquiryResult ?? "no result"}, {refund.RecoveryAttempts} recovery attempt(s)"));
        }

        return new PaymentDetail(attempts, refunds, [.. timeline.OrderBy(entry => entry.At).ThenBy(entry => entry.Kind)]);
    }

    public const string PspEventsSql = """
        select received_at, source, psp_event_id, outcome, payload::text
          from psp_events
         where payment_id = @id
         order by received_at, psp_event_id
        """;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
