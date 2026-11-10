using System.Text.Json;
using Npgsql;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

/// <summary>
/// The parked-answer payload (D59). It is plain JSON so the worker can rebuild the command
/// without a serializer contract on the domain records.
/// </summary>
public sealed record ParkedAnswer(
    string Kind,
    Guid? AttemptId,
    Guid? RefundId,
    string? Operation,
    string? Outcome,
    long? AmountMinor,
    string? Currency,
    string? FailureCode,
    string? ActionType,
    bool? Approved,
    bool? TimedOut,
    bool? ProvenNotCharged,
    DateTimeOffset OccurredAt)
{
    public static ParkedAnswer From(PaymentCommand command) => command switch
    {
        PaymentCommand.PspAnswer answer => new(
            "psp_answer", answer.AttemptId.Value, null, answer.Operation.ToString(),
            answer.Outcome.ToString(), answer.AnswerAmount?.Minor, answer.AnswerAmount?.Currency.Value,
            answer.FailureCode, answer.ActionType, null, null, null, answer.OccurredAt),
        PaymentCommand.RecoveryAnswer answer => new(
            "recovery_answer", answer.AttemptId.Value, null, answer.Operation.ToString(),
            answer.Outcome.ToString(), answer.AnswerAmount?.Minor, answer.AnswerAmount?.Currency.Value,
            answer.FailureCode, answer.ActionType, null, null, null, answer.OccurredAt),
        PaymentCommand.CaptureAnswer answer => new(
            "capture_answer", answer.CaptureAttemptId.Value, null, "Capture", null,
            answer.AnswerAmount?.Minor, answer.AnswerAmount?.Currency.Value, answer.FailureCode, null,
            answer.Approved, answer.TimedOut, null, answer.OccurredAt),
        PaymentCommand.VoidAnswer answer => new(
            "void_answer", answer.VoidAttemptId.Value, null, "Void", null, null, null,
            answer.FailureCode, null, answer.Approved, answer.TimedOut, null, answer.OccurredAt),
        PaymentCommand.RefundConfirm answer => new(
            "refund_confirm", null, answer.RefundId.Value, null, null,
            answer.AnswerAmount?.Minor, answer.AnswerAmount?.Currency.Value, null, null, null, null, null,
            answer.OccurredAt),
        PaymentCommand.RefundRelease answer => new(
            "refund_release", null, answer.RefundId.Value, null, null, null, null,
            answer.FailureCode, null, null, null, answer.ProvenNotCharged, answer.OccurredAt),
        PaymentCommand.RefundMismatch answer => new(
            "refund_mismatch", null, answer.RefundId.Value, null, null,
            answer.AnswerAmount?.Minor, answer.AnswerAmount?.Currency.Value, null, null, null, null, null,
            answer.OccurredAt),
        _ => throw new InvalidOperationException($"Command {command.GetType().Name} can't be parked."),
    };

    public static ParkedAnswer Deserialize(string payload) =>
        JsonSerializer.Deserialize<ParkedAnswer>(payload)
        ?? throw new InvalidOperationException("Empty parked answer.");

    public string Serialize() => JsonSerializer.Serialize(this);

    public PaymentCommand ToCommand()
    {
        Money? Money() => AmountMinor is { } amount && Currency is { } currency
            ? new Money(amount, CurrencyCode.Parse(currency))
            : null;
        var at = OccurredAt;
        return Kind switch
        {
            "psp_answer" => new PaymentCommand.PspAnswer(
                new AttemptId(AttemptId!.Value), Enum.Parse<AttemptOperation>(Operation!),
                Enum.Parse<PspAnswerOutcome>(Outcome!), Money(), FailureCode, ActionType, at),
            "recovery_answer" => new PaymentCommand.RecoveryAnswer(
                new AttemptId(AttemptId!.Value), Enum.Parse<AttemptOperation>(Operation!),
                Enum.Parse<RecoveryOutcome>(Outcome!), Money(), FailureCode, ActionType, at),
            "capture_answer" => new PaymentCommand.CaptureAnswer(
                new AttemptId(AttemptId!.Value), Approved ?? false, Money(), FailureCode, TimedOut ?? false, at),
            "void_answer" => new PaymentCommand.VoidAnswer(
                new AttemptId(AttemptId!.Value), Approved ?? false, TimedOut ?? false, FailureCode, at),
            "refund_confirm" => new PaymentCommand.RefundConfirm(
                new RefundId(RefundId!.Value), Money(), at),
            "refund_release" => new PaymentCommand.RefundRelease(
                new RefundId(RefundId!.Value), ProvenNotCharged ?? false, FailureCode, at),
            "refund_mismatch" => new PaymentCommand.RefundMismatch(
                new RefundId(RefundId!.Value), Money(), at),
            _ => throw new InvalidOperationException($"Unknown parked answer '{Kind}'."),
        };
    }
}

/// <summary>
/// The PSP event log's writer (D59). A PSP answer that transaction B can't apply is parked here
/// in its own short transaction, so it is never dropped.
/// </summary>
public sealed class PspEventStore(TxRunner runner)
{
    public async Task<string> ParkApiAnswerAsync(
        Guid account,
        OperationRef operation,
        PaymentCommand answer,
        CancellationToken cancellationToken)
    {
        var attempt = operation as OperationRef.Attempt;
        var refund = operation as OperationRef.Refund;
        var eventId = attempt is not null
            ? $"api:attempt:{attempt.Id.Value}"
            : $"api:refund:{refund!.Id.Value}";
        var payload = ParkedAnswer.From(answer).Serialize();

        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into psp_events
              (psp_account_id, psp_event_id, source, payload, payment_id, attempt_id, refund_id,
               outcome, next_attempt_at)
            values
              (@account, @event, 'api_answer', @payload::jsonb,
               coalesce(
                 (select payment_id from payment_attempts where attempt_id = @attempt),
                 (select payment_id from refunds where id = @refund)),
               @attempt, @refund, 'parked', now())
            on conflict (psp_account_id, psp_event_id) do nothing
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue(
            "attempt", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)attempt?.Id.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "refund", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)refund?.Id.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return eventId;
    }
}
