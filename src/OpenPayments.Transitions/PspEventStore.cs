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
public enum EventOutcome
{
    Applied,
    Ignored,
    Contradiction,
    Parked,
    Duplicate,
    Expired,
}

public sealed class PspEventStore(TxRunner runner)
{
    private PaymentStore? _store;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _outcomes = new(StringComparer.Ordinal);
    private long _parked;
    private long _oldestParkedTicks;

    public IReadOnlyDictionary<string, long> OutcomeCounts => _outcomes;

    public long ParkedCount => Interlocked.Read(ref _parked);

    public DateTimeOffset? OldestParkedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _oldestParkedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private void Count(string outcome) =>
        _outcomes.AddOrUpdate("callback:" + outcome, 1, static (_, count) => count + 1);

    private void CountParked()
    {
        Interlocked.Increment(ref _parked);
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var current = Interlocked.Read(ref _oldestParkedTicks);
        while ((current == 0 || now < current)
            && Interlocked.CompareExchange(ref _oldestParkedTicks, now, current) != current)
        {
            current = Interlocked.Read(ref _oldestParkedTicks);
        }
    }

    internal void Attach(PaymentStore store) => _store = store;


    /// <summary>
    /// D30/D59: insert the event and apply what it says in one transaction; a duplicate changes
    /// nothing, an event the operation can't take yet is parked, and a contradiction is recorded.
    /// </summary>
    public async Task<EventOutcome> ReceiveAsync(
        Guid pspAccountId,
        PspEvent pspEvent,
        string payload,
        CancellationToken cancellationToken)
    {
        var (merchant, paymentId) = await ResolveAsync(pspEvent.Reference, cancellationToken);
        var merchantId = merchant ?? new MerchantId(Guid.Empty);
        return await runner.RunAsync(
            merchantId,
            async (connection, transaction, ct) =>
            {
                if (!await InsertAsync(connection, transaction, pspAccountId, pspEvent, payload, paymentId, ct))
                {
                    Count("duplicate");
                    return EventOutcome.Duplicate;
                }

                if (paymentId is null || _store is null)
                {
                    Count("parked");
                    CountParked();
                    return EventOutcome.Parked;
                }

                var view = await PaymentStore.LoadPaymentAsync(paymentId.Value, merchantId, ct, connection, transaction);
                if (view is null)
                {
                    Count("parked");
                    CountParked();
                    return EventOutcome.Parked;
                }

                var (command, contradiction) = Map(pspEvent, view);
                if (contradiction)
                {
                    await MarkAsync(connection, transaction, pspAccountId, pspEvent.EventId, "contradiction", ct);
                    Count("contradiction");
                    return EventOutcome.Contradiction;
                }

                if (command is null)
                {
                    await MarkAsync(connection, transaction, pspAccountId, pspEvent.EventId, "ignored", ct);
                    Count("ignored");
                    return EventOutcome.Ignored;
                }

                return await ApplyCommandAndMarkAsync(
                    connection, transaction, merchantId, paymentId.Value,
                    pspAccountId, pspEvent.EventId, command, ct);
            },
            cancellationToken);
    }

    private async Task<EventOutcome> ApplyCommandAndMarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchantId,
        PaymentId paymentId,
        Guid pspAccountId,
        string eventId,
        PaymentCommand command,
        CancellationToken ct)
    {
        var view = await PaymentStore.LoadPaymentAsync(
            paymentId, merchantId, ct, connection, transaction);
        if (view is null)
        {
            await ParkAsync(connection, transaction, pspAccountId, eventId, ct);
            Count("parked");
            CountParked();
            return EventOutcome.Parked;
        }

        var beforeState = view.State;
        var beforeAttempt = view.Attempt?.State;
        var beforeRefund = view.Refund?.State;
        var result = await _store!.ApplyInTransactionAsync(
            connection, transaction, paymentId, merchantId, command, null, ct);
        if (result is not ApplyResult.Applied applied)
        {
            await ParkAsync(connection, transaction, pspAccountId, eventId, ct);
            Count("parked");
            CountParked();
            return EventOutcome.Parked;
        }

        var changed = applied.Payment.State != beforeState
            || applied.Payment.Attempt?.State != beforeAttempt
            || applied.Payment.Refund?.State != beforeRefund;
        await MarkAsync(
            connection, transaction, pspAccountId, eventId,
            changed ? "applied" : "ignored", ct);
        Count(changed ? "applied" : "ignored");
        return changed ? EventOutcome.Applied : EventOutcome.Ignored;
    }

    /// <summary>
    /// Re-applies a parked row from its stored payload (Task 26). The row's source decides how:
    /// an <c>api_answer</c> row holds the parked command, a <c>callback</c> row holds the raw event.
    /// Past the parked limit the row expires and the operation is left to the reconciler.
    /// </summary>
    public async Task<EventOutcome> ReplayAsync(
        Guid pspAccountId,
        string eventId,
        DateTimeOffset now,
        TimeSpan parkedLimit,
        CancellationToken cancellationToken)
    {
        var (source, payload, storedPayment, receivedAt) = await LoadParkedAsync(
            pspAccountId, eventId, cancellationToken);
        if (source is null)
        {
            return EventOutcome.Ignored;
        }

        if (now - receivedAt > parkedLimit)
        {
            await ExpireAsync(pspAccountId, eventId, cancellationToken);
            return EventOutcome.Expired;
        }

        if (source == "api_answer")
        {
            PaymentCommand command;
            try
            {
                command = ParkedAnswer.Deserialize(payload).ToCommand();
            }
            catch (InvalidOperationException)
            {
                await ExpireAsync(pspAccountId, eventId, cancellationToken);
                return EventOutcome.Expired;
            }

            var merchant = storedPayment is { } payment
                ? await MerchantForPaymentAsync(payment, cancellationToken)
                : null;
            if (merchant is null || storedPayment is null)
            {
                return EventOutcome.Parked;
            }

            return await runner.RunAsync(
                merchant.Value,
                (connection, transaction, ct) => ApplyCommandAndMarkAsync(
                    connection, transaction, merchant.Value, storedPayment.Value,
                    pspAccountId, eventId, command, ct),
                cancellationToken);
        }

        var parsed = PspEventJson.Parse(payload);
        if (parsed.IsFailure)
        {
            await ExpireAsync(pspAccountId, eventId, cancellationToken);
            return EventOutcome.Expired;
        }

        var (resolvedMerchant, resolvedPayment) = await ResolveAsync(parsed.Value.Reference, cancellationToken);
        var paymentId = storedPayment ?? resolvedPayment;
        if (resolvedMerchant is null || paymentId is null)
        {
            return EventOutcome.Parked;
        }

        return await runner.RunAsync(
            resolvedMerchant.Value,
            async (connection, transaction, ct) =>
            {
                var view = await PaymentStore.LoadPaymentAsync(
                    paymentId.Value, resolvedMerchant.Value, ct, connection, transaction);
                if (view is null)
                {
                    await ParkAsync(connection, transaction, pspAccountId, eventId, ct);
                    Count("parked");
                    CountParked();
                    return EventOutcome.Parked;
                }

                var (command, contradiction) = Map(parsed.Value, view);
                if (contradiction)
                {
                    await MarkAsync(connection, transaction, pspAccountId, eventId, "contradiction", ct);
                    Count("contradiction");
                    return EventOutcome.Contradiction;
                }

                if (command is null)
                {
                    await MarkAsync(connection, transaction, pspAccountId, eventId, "ignored", ct);
                    Count("ignored");
                    return EventOutcome.Ignored;
                }

                return await ApplyCommandAndMarkAsync(
                    connection, transaction, resolvedMerchant.Value, paymentId.Value,
                    pspAccountId, eventId, command, ct);
            },
            cancellationToken);
    }

    private async Task<(string? Source, string Payload, PaymentId? Payment, DateTimeOffset ReceivedAt)> LoadParkedAsync(
        Guid account, string eventId, CancellationToken ct)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select source, payload::text, payment_id, received_at
            from psp_events
            where psp_account_id = @account and psp_event_id = @event and processed_at is null
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, string.Empty, null, default);
        }

        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : new PaymentId(reader.GetGuid(2)),
            reader.GetDateTime(3));
    }

    private async Task<MerchantId?> MerchantForPaymentAsync(PaymentId paymentId, CancellationToken ct)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "select merchant_id from payments where id = @id", connection);
        command.Parameters.AddWithValue("id", paymentId.Value);
        var value = await command.ExecuteScalarAsync(ct);
        return value is Guid merchant ? new MerchantId(merchant) : null;
    }

    private async Task ExpireAsync(Guid account, string eventId, CancellationToken ct)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            update psp_events
               set processed_at = now(), outcome = 'expired'
             where psp_account_id = @account and psp_event_id = @event and processed_at is null
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<(MerchantId? Merchant, PaymentId? Payment)> ResolveAsync(
        PspEventReference reference, CancellationToken ct)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select p.merchant_id, p.id
            from payment_attempts a join payments p on p.id = a.payment_id
            where a.attempt_id = @attempt
            union all
            select merchant_id, payment_id from refunds where id = @refund
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("attempt", NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)reference.Attempt?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("refund", NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)reference.Refund?.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (new MerchantId(reader.GetGuid(0)), new PaymentId(reader.GetGuid(1)))
            : (null, null);
    }

    private static async Task<bool> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account,
        PspEvent e, string payload, PaymentId? paymentId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into psp_events
              (psp_account_id, psp_event_id, source, payload, payment_id, attempt_id, refund_id)
            values (@account, @event, 'callback', @payload::jsonb, @payment, @attempt, @refund)
            on conflict (psp_account_id, psp_event_id) do nothing
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", e.EventId);
        command.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("payment", NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)paymentId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt", NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)e.Reference.Attempt?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("refund", NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)e.Reference.Refund?.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task MarkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, string eventId,
        string outcome, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            update psp_events
               set processed_at = now(), outcome = @outcome, attempts = attempts + 1
             where psp_account_id = @account and psp_event_id = @event
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("outcome", outcome);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ParkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid account, string eventId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            update psp_events
               set attempts = attempts + 1,
                   next_attempt_at = now() + make_interval(secs => least(60, power(2, attempts))),
                   last_error = 'not_applicable'
             where psp_account_id = @account and psp_event_id = @event and processed_at is null
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static (PaymentCommand? Command, bool Contradiction) Map(PspEvent e, PaymentView view)
    {
        var at = DateTimeOffset.UtcNow;
        if (e.Reference.Refund is { } refundId)
        {
            var refund = view.Refund;
            if (refund is null || refund.Id != refundId)
            {
                return (null, false);
            }

            return e.Kind switch
            {
                PspEventKind.Refunded when refund.State == RefundState.Pending =>
                    (new PaymentCommand.RefundConfirm(refundId, e.Amount, at), false),
                PspEventKind.Refunded => (null, false),
                PspEventKind.RefundFailed when refund.State == RefundState.Pending =>
                    (new PaymentCommand.RefundRelease(refundId, ProvenNotCharged: true, e.FailureCode, at), false),
                PspEventKind.RefundFailed => (null, false),
                _ => (null, false),
            };
        }

        var attempt = view.Attempt;
        if (attempt is null || attempt.Id != e.Reference.Attempt)
        {
            return (null, false);
        }

        return e.Kind switch
        {
            PspEventKind.Authorized when attempt.State is AttemptState.Pending or AttemptState.ActionRequired =>
                (new PaymentCommand.PspAnswer(attempt.Id, attempt.Operation, PspAnswerOutcome.Approved, e.Amount, null, null, at), false),
            PspEventKind.Authorized when attempt.State == AttemptState.Failed =>
                (new PaymentCommand.LateApproval(attempt.Id, attempt.Operation, e.Amount, null, at), false),
            PspEventKind.Authorized => (null, false),
            PspEventKind.SaleCaptured when attempt.State is AttemptState.Pending or AttemptState.ActionRequired =>
                (new PaymentCommand.PspAnswer(attempt.Id, attempt.Operation, PspAnswerOutcome.Approved, e.Amount, null, null, at), false),
            PspEventKind.Captured when attempt.Operation == AttemptOperation.Capture && attempt.State == AttemptState.Pending =>
                (new PaymentCommand.CaptureAnswer(attempt.Id, true, e.Amount, null, false, at), false),
            PspEventKind.Declined when attempt.State is AttemptState.Pending or AttemptState.ActionRequired =>
                (new PaymentCommand.PspAnswer(attempt.Id, attempt.Operation, PspAnswerOutcome.Declined, null, e.FailureCode, null, at), false),
            PspEventKind.Declined when attempt.State == AttemptState.Succeeded => (null, true),
            PspEventKind.ActionRequired when attempt.State is AttemptState.Pending or AttemptState.ActionRequired =>
                (new PaymentCommand.PspAnswer(attempt.Id, attempt.Operation, PspAnswerOutcome.RequiresAction, e.Amount, null, e.ActionType, at), false),
            PspEventKind.Voided when attempt.Operation == AttemptOperation.Void && attempt.State == AttemptState.Pending =>
                (new PaymentCommand.VoidAnswer(attempt.Id, true, false, null, at), false),
            PspEventKind.VoidFailed when attempt.Operation == AttemptOperation.Void && attempt.State == AttemptState.Pending =>
                (new PaymentCommand.VoidAnswer(attempt.Id, false, false, e.FailureCode, at), false),
            _ => (null, false),
        };
    }

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
