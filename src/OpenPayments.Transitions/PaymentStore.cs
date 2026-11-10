using Npgsql;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

public sealed record StoredResponse(int StatusCode, string Body);

public sealed record AttemptView(
    AttemptId Id,
    AttemptOperation Operation,
    AttemptState State,
    Money Amount,
    AttemptId? TargetAttemptId,
    string? FailureCode,
    string Psp,
    Guid PspAccountId,
    string? IdempotencyKey = null,
    bool ResolvesKey = true);

public sealed record RefundView(
    RefundId Id,
    RefundState State,
    Money Amount,
    string? FailureCode,
    string Psp,
    Guid PspAccountId,
    DateTimeOffset CreatedAt = default,
    string? IdempotencyKey = null,
    bool ResolvesKey = true);

public sealed record RefundRecord(
    RefundId Id,
    PaymentId PaymentId,
    MerchantId MerchantId,
    RefundView Refund);

public sealed record PaymentView(
    PaymentId Id,
    MerchantId MerchantId,
    PaymentState State,
    Money Amount,
    long CapturedMinor,
    long RefundedMinor,
    long RefundPendingMinor,
    int AttemptCount,
    int EventSeq,
    int Version,
    AttemptView? Attempt,
    RefundView? Refund,
    DateTimeOffset ExpiresAt = default,
    DateTimeOffset CreatedAt = default)
{
    public PaymentSnapshot ToSnapshot() =>
        new(
            Id,
            MerchantId,
            State,
            Amount,
            CapturedMinor,
            RefundedMinor,
            RefundPendingMinor,
            AttemptCount,
            EventSeq,
            Attempt is null
                ? null
                : new AttemptSnapshot(
                    Attempt.Id,
                    Attempt.Operation,
                    Attempt.State,
                    Attempt.Amount,
                    Attempt.TargetAttemptId,
                    Attempt.FailureCode),
            Refund is null
                ? null
                : new RefundSnapshot(Refund.Id, Refund.State, Refund.Amount, Refund.FailureCode),
            LiveCapture: Attempt is { Operation: AttemptOperation.Capture, State: AttemptState.Pending or AttemptState.Unknown },
            LiveVoid: Attempt is { Operation: AttemptOperation.Void, State: AttemptState.Pending or AttemptState.Unknown },
            HoldPosted: State is PaymentState.Authorized or PaymentState.Review && Attempt is { Operation: AttemptOperation.Authorize, State: AttemptState.Succeeded });
}

public sealed record BeginAttempt(
    MerchantId MerchantId,
    string Key,
    byte[] RequestHash,
    PaymentId PaymentId,
    Money Amount,
    AttemptOperation Operation,
    bool CreatePayment,
    DateTimeOffset ExpiresAt,
    string Psp = "mock",
    Guid? PspAccountId = null,
    string MethodRef = "tok_test",
    string MethodType = "card",
    bool ResolvesKey = true);

public abstract record KeyLookup
{
    public sealed record NotFound : KeyLookup;
    public sealed record Finished(StoredResponse Response) : KeyLookup;
    // The key has its payment, or refund, but no stored answer: the operation is running, or it
    // ended without one (an unknown outcome, or a crash before the answer was stored).
    public sealed record Unresolved(PaymentView Payment, RefundId? Refund = null) : KeyLookup;
    public sealed record InFlight : KeyLookup;
    public sealed record HashMismatch : KeyLookup;
}

public abstract record BeginResult
{
    public sealed record Started(AttemptId AttemptId, PaymentView Payment) : BeginResult;
    public sealed record Created(PaymentView Payment) : BeginResult;
    public sealed record Replay(StoredResponse Response) : BeginResult;
    public sealed record KeyInFlight : BeginResult;
    // D31, E27: an unresolved key replays the operation's current state.
    public sealed record Pending(PaymentView Payment, RefundId? Refund) : BeginResult;
    public sealed record HashMismatch : BeginResult;
    public sealed record LiveAttemptExists(PaymentView Payment) : BeginResult;
    public sealed record CapExceeded(PaymentView Payment) : BeginResult;
    public sealed record Rejected(string ReasonCode, PaymentView? Payment = null) : BeginResult;
}

public abstract record BeginOperationResult
{
    public sealed record Started(PaymentView Payment) : BeginOperationResult;
    public sealed record Replay(StoredResponse Response) : BeginOperationResult;
    public sealed record KeyInFlight : BeginOperationResult;
    public sealed record Pending(PaymentView Payment, RefundId? Refund) : BeginOperationResult;
    public sealed record HashMismatch : BeginOperationResult;
    public sealed record Rejected(string ReasonCode, PaymentView? Payment = null) : BeginOperationResult;
}

public sealed record MerchantPspAccount(
    Guid Id,
    string Psp,
    string Mode,
    string CredentialRef,
    int RateLimitPerSecond);

/// <summary>
/// The idempotent answer for <paramref name="Key"/>, rendered from the payment as transaction B
/// leaves it and written in that same transaction (D58). <paramref name="Render"/> returns null
/// when the caller would not resolve the key, so the key stays unresolved as before.
/// </summary>
public sealed record KeyAnswer(string Key, Func<PaymentView, StoredResponse?> Render);

public abstract record ApplyResult
{
    public sealed record Applied(PaymentView Payment) : ApplyResult;
    public sealed record AlreadyMoved(PaymentView Payment) : ApplyResult;
    public sealed record Rejected(string ReasonCode, PaymentView Payment) : ApplyResult;
    // D59: the PSP's answer could not be applied even after a fresh retry, so it is parked and
    // the client gets 202, never a refusal after the PSP acted.
    public sealed record Parked(PaymentView Payment) : ApplyResult;
}

/// <summary>
/// The database shell around <see cref="PaymentRules"/>. It loads the snapshot, calls the pure
/// decision, and writes state, ledger and outbox in one transaction. It holds no business rules.
/// </summary>
public sealed class PaymentStore
{
    /// <summary>
    /// No request outlives this, so a key with no answer that is older belongs to a request that
    /// has ended. Derived from the longest path, not from D34's 5 s budget, which no timer
    /// enforces yet (review N2): a two-call auto-capture makes two PSP calls of at most 2.5 s and
    /// up to four commits that may each wait 3 s, about 17 s. 30 s leaves room for retries.
    /// </summary>
    public static readonly TimeSpan InFlightWindow = TimeSpan.FromSeconds(30);

    private readonly TxRunner _runner;
    private readonly PspEventStore _events;

    public PaymentStore(TxRunner runner, PspEventStore? events = null)
    {
        _runner = runner;
        _events = events ?? new PspEventStore(runner);
    }

    /// <summary>Test-only failpoint used by the atomicity test.</summary>
    public Action? BeforeOutboxInsert { get; set; }

    /// <summary>Test-only failpoint used to simulate an unknown transaction A commit.</summary>
    public Action? BeforeBegin { get; set; }

    /// <summary>Test-only failpoint used for L8's first read after an authorization.</summary>
    public Action? BeforeRead { get; set; }

    /// <summary>Test-only failpoint after the snapshot is read and before the conditional update.</summary>
    public Action? BeforePaymentUpdate { get; set; }

    public async Task<KeyLookup> ReadKeyAsync(
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadKeyOnConnectionAsync(connection, null, merchant, key, requestHash, cancellationToken);
    }

    public async Task<BeginResult> BeginAttemptAsync(
        BeginAttempt command,
        CancellationToken cancellationToken)
    {
        var existing = await ReadKeyAsync(
            command.MerchantId, command.Key, command.RequestHash, cancellationToken);
        switch (existing)
        {
            case KeyLookup.Finished finished:
                return new BeginResult.Replay(finished.Response);
            case KeyLookup.HashMismatch:
                return new BeginResult.HashMismatch();
            case KeyLookup.Unresolved unresolved:
                return new BeginResult.Pending(unresolved.Payment, unresolved.Refund);
            case KeyLookup.InFlight:
                return new BeginResult.KeyInFlight();
        }

        try
        {
            BeforeBegin?.Invoke();
            return await _runner.RunAsync(
                command.MerchantId,
                (connection, transaction, ct) => BeginInTransactionAsync(
                    connection, transaction, command, ct),
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            var lookup = await ReadKeyAsync(
                command.MerchantId, command.Key, command.RequestHash, cancellationToken);
            switch (lookup)
            {
                case KeyLookup.Finished finished:
                    return new BeginResult.Replay(finished.Response);
                case KeyLookup.HashMismatch:
                    return new BeginResult.HashMismatch();
                case KeyLookup.Unresolved unresolved:
                    return new BeginResult.Pending(unresolved.Payment, unresolved.Refund);
                case KeyLookup.InFlight:
                    return new BeginResult.KeyInFlight();
            }

            var payment = await LoadPaymentAsync(
                command.PaymentId, command.MerchantId, cancellationToken);
            return payment is null
                ? new BeginResult.Rejected("not_found")
                : new BeginResult.LiveAttemptExists(payment);
        }
    }

    public async Task<ApplyResult> ApplyAsync(
        PaymentId paymentId,
        PaymentCommand command,
        StoredResponse? response,
        CancellationToken cancellationToken,
        KeyAnswer? keyAnswer = null)
    {
        var merchant = await MerchantForAsync(paymentId, cancellationToken);
        if (merchant is null)
        {
            return new ApplyResult.Rejected("not_found", null!);
        }

        return await _runner.RunAsync(
            merchant.Value,
            async (connection, transaction, ct) =>
            {
                return await ApplyInTransactionAsync(
                    connection, transaction, paymentId, merchant.Value, command, response, ct, keyAnswer);
            },
            cancellationToken);
    }

    /// <summary>
    /// D59/L7: apply a PSP answer; when it is refused or loses its conditional update, re-read
    /// and apply once more; if it is still refused, park it in <c>psp_events</c> and answer 202.
    /// </summary>
    public async Task<ApplyResult> ApplyPspAnswerAsync(
        PaymentId paymentId,
        PaymentCommand answer,
        KeyAnswer? keyAnswer,
        CancellationToken cancellationToken)
    {
        var first = await ApplyAsync(paymentId, answer, null, cancellationToken, keyAnswer);
        if (first is ApplyResult.Applied)
        {
            return first;
        }

        var second = await ApplyAsync(paymentId, answer, null, cancellationToken, keyAnswer);
        if (second is ApplyResult.Applied)
        {
            return second;
        }

        var view = second switch
        {
            ApplyResult.AlreadyMoved already => already.Payment,
            ApplyResult.Rejected rejected => rejected.Payment,
            _ => null,
        };
        if (view is null)
        {
            return second;
        }

        var account = view.Attempt?.PspAccountId ?? view.Refund?.PspAccountId;
        var operation = OperationFor(answer);
        if (account is { } pspAccount && operation is not null)
        {
            try
            {
                await _events.ParkApiAnswerAsync(pspAccount, operation, answer, cancellationToken);
            }
#pragma warning disable CA1031 // D59: the answer is 202 even when the park write also fails.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return new ApplyResult.Parked(view);
    }

    private static OperationRef? OperationFor(PaymentCommand answer) => answer switch
    {
        PaymentCommand.PspAnswer psp => new OperationRef.Attempt(psp.AttemptId, psp.Operation),
        PaymentCommand.RecoveryAnswer recovery => new OperationRef.Attempt(recovery.AttemptId, recovery.Operation),
        PaymentCommand.CaptureAnswer capture => new OperationRef.Attempt(
            capture.CaptureAttemptId, AttemptOperation.Capture),
        PaymentCommand.VoidAnswer voidAnswer => new OperationRef.Attempt(
            voidAnswer.VoidAttemptId, AttemptOperation.Void),
        PaymentCommand.RefundConfirm confirm => new OperationRef.Refund(confirm.RefundId),
        PaymentCommand.RefundRelease release => new OperationRef.Refund(release.RefundId),
        PaymentCommand.RefundMismatch mismatch => new OperationRef.Refund(mismatch.RefundId),
        _ => null,
    };

    private static async Task ResolveKeyInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        string key,
        StoredResponse response,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update idempotency_keys
               set response_code = @code, response_body = @body::jsonb, resolved_at = now()
             where merchant_id = @merchant and key = @key and resolved_at is null
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("code", response.StatusCode);
        command.Parameters.AddWithValue("body", NpgsqlTypes.NpgsqlDbType.Jsonb, response.Body);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<BeginResult> BeginInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BeginAttempt command,
        CancellationToken cancellationToken)
    {
        PaymentView? view = null;
        PaymentSnapshot snapshot;
        if (command.CreatePayment)
        {
            snapshot = new PaymentSnapshot(
                command.PaymentId,
                command.MerchantId,
                PaymentState.AwaitingMethod,
                command.Amount);
        }
        else
        {
            view = await LoadPaymentAsync(
                command.PaymentId, command.MerchantId, cancellationToken, connection, transaction, forUpdate: true);
            if (view is null)
            {
                return new BeginResult.Rejected("not_found");
            }

            if (view.State is PaymentState.Refunded or PaymentState.Canceled
                or PaymentState.Voided or PaymentState.Expired)
            {
                return new BeginResult.Rejected("invalid_state", view);
            }

            if (view.Attempt is { State: AttemptState.Pending or AttemptState.ActionRequired
                or AttemptState.Unknown or AttemptState.Succeeded })
            {
                return new BeginResult.LiveAttemptExists(view);
            }

            snapshot = view.ToSnapshot();
        }

        var attemptId = AttemptId.New();
        var transition = command.Operation == AttemptOperation.Sale
            ? PaymentRules.Decide(
                snapshot,
                new PaymentCommand.Sale(attemptId, command.ExpiresAt),
                RuleVersion.V1)
            : command.CreatePayment
                ? PaymentRules.Decide(
                    snapshot,
                    new PaymentCommand.CreatePayment(
                        command.PaymentId,
                        command.MerchantId,
                        command.Amount,
                        HasMethod: true,
                        command.Operation,
                        command.ExpiresAt,
                        AttemptId: attemptId),
                    RuleVersion.V1)
                : PaymentRules.Decide(
                    snapshot,
                    new PaymentCommand.Attempt(attemptId, command.ExpiresAt),
                    RuleVersion.V1);

        if (transition is TransitionResult.Rejected rejected)
        {
            return rejected.ReasonCode == "attempt_cap_reached" && view is not null
                ? new BeginResult.CapExceeded(view)
                : new BeginResult.Rejected(rejected.ReasonCode, view);
        }

        if (transition is not TransitionResult.Accepted accepted || accepted.Attempt is null)
        {
            return new BeginResult.Rejected("invalid_state", view);
        }

        var pspAccountId = command.PspAccountId
            ?? await ResolvePspAccountAsync(connection, transaction, command.MerchantId, cancellationToken);

        if (!await InsertKeyAsync(
                connection, transaction, command.MerchantId, command.Key,
                command.RequestHash, command.PaymentId, attemptId, null, cancellationToken))
        {
            return await ReadKeyOnConnectionAsync(
                connection, transaction, command.MerchantId, command.Key, command.RequestHash,
                cancellationToken) switch
            {
                KeyLookup.Finished finished => new BeginResult.Replay(finished.Response),
                KeyLookup.HashMismatch => new BeginResult.HashMismatch(),
                KeyLookup.Unresolved unresolved => new BeginResult.Pending(unresolved.Payment, unresolved.Refund),
                _ => new BeginResult.KeyInFlight(),
            };
        }

        if (command.CreatePayment)
        {
            await using var insertPayment = new NpgsqlCommand(
                """
                insert into payments
                  (id, merchant_id, state, amount_minor, currency, captured_minor,
                   refunded_minor, refund_pending_minor, attempt_count, event_seq, expires_at, version)
                values
                  (@id, @merchant, @state, @amount, @currency, 0, 0, 0, 1, 0, @expires, 0)
                """,
                connection,
                transaction);
            insertPayment.Parameters.AddWithValue("id", command.PaymentId.Value);
            insertPayment.Parameters.AddWithValue("merchant", command.MerchantId.Value);
            insertPayment.Parameters.AddWithValue("state", StateText.Payment(accepted.NewState));
            insertPayment.Parameters.AddWithValue("amount", command.Amount.Minor);
            insertPayment.Parameters.AddWithValue("currency", command.Amount.Currency.Value);
            insertPayment.Parameters.AddWithValue("expires", command.ExpiresAt);
            await insertPayment.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var updatePayment = new NpgsqlCommand(
                """
                update payments
                   set state = @state, attempt_count = attempt_count + 1,
                       version = version + 1, updated_at = now()
                 where id = @id and merchant_id = @merchant
                   and state = @old_state and version = @version
                """,
                connection,
                transaction);
            updatePayment.Parameters.AddWithValue("id", command.PaymentId.Value);
            updatePayment.Parameters.AddWithValue("merchant", command.MerchantId.Value);
            updatePayment.Parameters.AddWithValue("state", StateText.Payment(accepted.NewState));
            updatePayment.Parameters.AddWithValue("old_state", StateText.Payment(view!.State));
            updatePayment.Parameters.AddWithValue("version", view.Version);
            if (await updatePayment.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                _runner.Metrics.ConditionalUpdateZeroRow();
                return new BeginResult.Rejected("already_moved", view);
            }
        }

        await InsertAttemptAsync(
            connection,
            transaction,
            command.PaymentId,
            command.MerchantId,
            accepted.Attempt,
            pspAccountId,
            command.Psp,
            command.MethodRef,
            command.MethodType,
            command.Key,
            command.ResolvesKey,
            cancellationToken);

        var started = await LoadPaymentAsync(
            command.PaymentId, command.MerchantId, cancellationToken, connection, transaction);
        return new BeginResult.Started(attemptId, started!);
    }

    private async Task<ApplyResult> ApplyInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentId paymentId,
        MerchantId merchant,
        PaymentCommand command,
        StoredResponse? response,
        CancellationToken cancellationToken,
        KeyAnswer? keyAnswer = null)
    {
        var view = await LoadPaymentAsync(
            paymentId, merchant,
            cancellationToken, connection, transaction);
        if (view is null)
        {
            return new ApplyResult.Rejected("not_found", null!);
        }

        var transition = PaymentRules.Decide(view.ToSnapshot(), command, RuleVersion.V1);
        if (transition is TransitionResult.Rejected rejected)
        {
            return new ApplyResult.Rejected(rejected.ReasonCode, view);
        }

        if (transition is TransitionResult.NoOp)
        {
            return new ApplyResult.Applied(view);
        }

        var accepted = (TransitionResult.Accepted)transition;
        var applied = await PersistAsync(
            connection, transaction, view, command, accepted, response, cancellationToken);
        if (applied is ApplyResult.Applied done)
        {
            await ResolveFinishedOperationAsync(
                connection, transaction, done.Payment, accepted, keyAnswer, cancellationToken);
        }

        return applied;
    }

    /// <summary>
    /// D61: the transaction that finishes an attempt or refund resolves the key that started it,
    /// whoever finished it. The row carries the key and whether it resolves; a two-call
    /// authorize's row does not, so the capture's row is the one that resolves the create key.
    /// </summary>
    private static async Task ResolveFinishedOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView payment,
        TransitionResult.Accepted accepted,
        KeyAnswer? keyAnswer,
        CancellationToken cancellationToken)
    {
        OperationRef? operation = null;
        string? key = null;
        var resolves = false;
        if (accepted.Attempt is { State: AttemptState.Succeeded or AttemptState.Failed } attempt
            && payment.Attempt is { } attemptRow
            && attemptRow.Id == attempt.Id)
        {
            operation = new OperationRef.Attempt(attemptRow.Id, attemptRow.Operation);
            key = attemptRow.IdempotencyKey;
            resolves = attemptRow.ResolvesKey;
        }
        else if (accepted.Refund is { State: RefundState.Succeeded or RefundState.Failed } refund
            && payment.Refund is { } refundRow
            && refundRow.Id == refund.Id)
        {
            operation = new OperationRef.Refund(refundRow.Id);
            key = refundRow.IdempotencyKey;
            resolves = refundRow.ResolvesKey;
        }

        if (!resolves || key is null || operation is null)
        {
            return;
        }

        // The request path may override the body it would have stored before D61; a callback or
        // the reconciler uses the one renderer.
        var response = keyAnswer?.Render(payment) ?? AnswerRenderer.Render(payment, operation);
        await ResolveKeyInTransactionAsync(
            connection, transaction, payment.MerchantId, key, response, cancellationToken);
    }

    private async Task<ApplyResult> PersistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView view,
        PaymentCommand command,
        TransitionResult.Accepted accepted,
        StoredResponse? response,
        CancellationToken cancellationToken,
        string? idempotencyKey = null,
        bool resolvesKey = false)
    {
        var paymentId = view.Id;
        var captured = accepted.NewState is PaymentState.Captured
            or PaymentState.PartiallyRefunded
            or PaymentState.Refunded
            ? Math.Max(view.CapturedMinor, view.Amount.Minor)
            : view.CapturedMinor;
        var refunded = view.RefundedMinor;
        var pending = view.RefundPendingMinor;
        switch (command)
        {
            case PaymentCommand.RefundReserve reserve:
                pending += reserve.Amount.Minor;
                break;
            case PaymentCommand.RefundConfirm:
                var amount = view.Refund?.Amount.Minor ?? 0;
                refunded += amount;
                pending = Math.Max(0, pending - amount);
                break;
            case PaymentCommand.RefundRelease:
                pending = Math.Max(0, pending - (view.Refund?.Amount.Minor ?? 0));
                break;
        }

        var eventSequence = accepted.Events.Count == 0
            ? view.EventSeq
            : accepted.Events.Max(outbox => outbox.Sequence);
        BeforePaymentUpdate?.Invoke();
        await using (var update = new NpgsqlCommand(
            """
            update payments
               set state = @state,
                   captured_minor = @captured,
                   refunded_minor = @refunded,
                   refund_pending_minor = @pending,
                   event_seq = @event_seq,
                   version = version + 1,
                   updated_at = now()
             where id = @id and merchant_id = @merchant
               and state = @old_state and version = @version
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("state", StateText.Payment(accepted.NewState));
            update.Parameters.AddWithValue("captured", captured);
            update.Parameters.AddWithValue("refunded", refunded);
            update.Parameters.AddWithValue("pending", pending);
            update.Parameters.AddWithValue("event_seq", (int)eventSequence);
            update.Parameters.AddWithValue("id", paymentId.Value);
            update.Parameters.AddWithValue("merchant", view.MerchantId.Value);
            update.Parameters.AddWithValue("old_state", StateText.Payment(view.State));
            update.Parameters.AddWithValue("version", view.Version);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                _runner.Metrics.ConditionalUpdateZeroRow();
                var current = await LoadPaymentAsync(
                    paymentId, view.MerchantId, cancellationToken, connection, transaction);
                return new ApplyResult.AlreadyMoved(current!);
            }
        }

        await InsertLedgerAsync(connection, transaction, view, paymentId, accepted.Ledger, ct: cancellationToken);
        BeforeOutboxInsert?.Invoke();
        await InsertOutboxAsync(connection, transaction, view, accepted.Events, cancellationToken);
        await UpdateAttemptAsync(
            connection, transaction, view, accepted.Attempt, idempotencyKey, resolvesKey, cancellationToken);
        await UpdateRefundAsync(
            connection, transaction, view, accepted.Refund, command, idempotencyKey, resolvesKey, cancellationToken);
        _ = response;

        var updated = await LoadPaymentAsync(
            paymentId, view.MerchantId, cancellationToken, connection, transaction);
        return new ApplyResult.Applied(updated!);
    }

    private static async Task<bool> InsertKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        PaymentId paymentId,
        AttemptId? attemptId,
        RefundId? refundId,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            insert into idempotency_keys
              (merchant_id, key, request_hash, payment_id, attempt_id, refund_id,
               response_code, response_body, resolved_at)
            values
              (@merchant, @key, @hash, @payment, @attempt, @refund, null, null, null)
            on conflict (merchant_id, key) do nothing
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("merchant", merchant.Value);
        insert.Parameters.AddWithValue("key", key);
        insert.Parameters.AddWithValue("hash", requestHash);
        insert.Parameters.AddWithValue("payment", paymentId.Value);
        insert.Parameters.AddWithValue("attempt", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)attemptId?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("refund", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)refundId?.Value ?? DBNull.Value);
        return await insert.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task InsertAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentId paymentId,
        MerchantId merchantId,
        AttemptChange attempt,
        Guid pspAccountId,
        string psp,
        string methodRef,
        string methodType,
        string? idempotencyKey,
        bool resolvesKey,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id,
               target_attempt_id, state, failure_code, method_ref, method_type,
               idempotency_key, resolves_key, created_at)
            values
              (@attempt, @payment, @merchant, @operation, @psp, @psp_account,
               @target, @state, @failure, @method_ref, @method_type,
               @key, @resolves_key, now())
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("attempt", attempt.Id.Value);
        insert.Parameters.AddWithValue("payment", paymentId.Value);
        insert.Parameters.AddWithValue("merchant", merchantId.Value);
        insert.Parameters.AddWithValue("operation", StateText.Operation(attempt.Operation));
        insert.Parameters.AddWithValue("psp", psp);
        insert.Parameters.AddWithValue("psp_account", pspAccountId);
        insert.Parameters.AddWithValue("target", (object?)attempt.VoidTargetAttemptId?.Value ?? DBNull.Value);
        insert.Parameters.AddWithValue("state", StateText.Attempt(attempt.State));
        insert.Parameters.AddWithValue("failure", (object?)attempt.FailureCode ?? DBNull.Value);
        insert.Parameters.AddWithValue("method_ref", (object?)methodRef ?? DBNull.Value);
        insert.Parameters.AddWithValue("method_type", (object?)methodType ?? DBNull.Value);
        insert.Parameters.AddWithValue("key", (object?)idempotencyKey ?? DBNull.Value);
        insert.Parameters.AddWithValue("resolves_key", resolvesKey);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView view,
        PaymentId paymentId,
        IReadOnlyList<LedgerLine> ledger,
        CancellationToken ct)
    {
        foreach (var group in ledger.GroupBy(line => line.TransactionId))
        {
            var first = group.First();
            await using (var txn = new NpgsqlCommand(
                """
                insert into ledger_transactions
                  (id, merchant_id, payment_id, kind, source_event_id, effective_at)
                values (@id, @merchant, @payment, @kind, @source, now())
                on conflict (id) do nothing
                """,
                connection,
                transaction))
            {
                txn.Parameters.AddWithValue("id", group.Key);
                txn.Parameters.AddWithValue("merchant", view.MerchantId.Value);
                txn.Parameters.AddWithValue("payment", paymentId.Value);
                txn.Parameters.AddWithValue("kind", first.TransactionKind);
                txn.Parameters.AddWithValue("source", group.Key);
                await txn.ExecuteNonQueryAsync(ct);
            }

            foreach (var line in group)
            {
                var accountId = await ResolveAccountAsync(
                    connection, transaction, view.MerchantId, line.AccountType, line.Amount.Currency, ct);
                await using var entry = new NpgsqlCommand(
                    """
                    insert into ledger_entries
                      (txn_id, entry_no, merchant_id, account_id, subject_id, direction,
                       amount_minor, currency)
                    values
                      (@txn, @entry, @merchant, @account, @subject, @direction, @amount, @currency)
                    on conflict (txn_id, entry_no) do nothing
                    """,
                    connection,
                    transaction);
                entry.Parameters.AddWithValue("txn", group.Key);
                entry.Parameters.AddWithValue("entry", line.EntryNo);
                entry.Parameters.AddWithValue("merchant", view.MerchantId.Value);
                entry.Parameters.AddWithValue("account", accountId);
                entry.Parameters.AddWithValue("subject", paymentId.Value);
                entry.Parameters.AddWithValue("direction", line.Direction.ToString());
                entry.Parameters.AddWithValue("amount", line.Amount.Minor);
                entry.Parameters.AddWithValue("currency", line.Amount.Currency.Value);
                await entry.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView view,
        IReadOnlyList<OutboxEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var outbox in events)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into outbox
                  (id, merchant_id, aggregate_id, event_type, sequence, schema_version, payload, created_at)
                values
                  (@id, @merchant, @aggregate, @event_type, @sequence, @schema_version, @payload::jsonb, now())
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("id", outbox.EventId);
            insert.Parameters.AddWithValue("merchant", view.MerchantId.Value);
            insert.Parameters.AddWithValue("aggregate", outbox.AggregateId);
            insert.Parameters.AddWithValue("event_type", outbox.EventType);
            insert.Parameters.AddWithValue("sequence", outbox.Sequence);
            insert.Parameters.AddWithValue("schema_version", outbox.SchemaVersion);
            insert.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, "{}");
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView view,
        AttemptChange? change,
        string? idempotencyKey,
        bool resolvesKey,
        CancellationToken ct)
    {
        if (change is null)
        {
            return;
        }

        if (view.Attempt is not null && view.Attempt.Id == change.Id)
        {
            await using var update = new NpgsqlCommand(
                """
                update payment_attempts
                   set state = @state, failure_code = @failure
                 where payment_id = @payment and attempt_id = @attempt
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("state", StateText.Attempt(change.State));
            update.Parameters.AddWithValue("failure", (object?)change.FailureCode ?? DBNull.Value);
            update.Parameters.AddWithValue("payment", view.Id.Value);
            update.Parameters.AddWithValue("attempt", change.Id.Value);
            await update.ExecuteNonQueryAsync(ct);
            return;
        }

        var psp = view.Attempt?.Psp ?? "mock";
        var pspAccount = view.Attempt?.PspAccountId
            ?? throw new InvalidOperationException("The payment has no PSP account.");
        await InsertAttemptAsync(
            connection,
            transaction,
            view.Id,
            view.MerchantId,
            change,
            pspAccount,
            psp,
            change.Operation is AttemptOperation.Authorize or AttemptOperation.Sale ? "tok_test" : null!,
            change.Operation is AttemptOperation.Authorize or AttemptOperation.Sale ? "card" : null!,
            idempotencyKey,
            resolvesKey,
            ct);
    }

    private static async Task UpdateRefundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentView view,
        RefundChange? change,
        PaymentCommand command,
        string? idempotencyKey,
        bool resolvesKey,
        CancellationToken ct)
    {
        if (change is null)
        {
            return;
        }

        if (view.Refund is not null && view.Refund.Id == change.Id)
        {
            await using var update = new NpgsqlCommand(
                "update refunds set state = @state where id = @id",
                connection,
                transaction);
            update.Parameters.AddWithValue("state", StateText.Refund(change.State));
            update.Parameters.AddWithValue("id", change.Id.Value);
            await update.ExecuteNonQueryAsync(ct);
            return;
        }

        var amount = command switch
        {
            PaymentCommand.RefundReserve reserve => reserve.Amount,
            _ => view.Amount,
        };
        var psp = view.Attempt?.Psp ?? "mock";
        var pspAccount = view.Attempt?.PspAccountId
            ?? throw new InvalidOperationException("The payment has no PSP account.");
        await using var insert = new NpgsqlCommand(
            """
            insert into refunds
              (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id,
               idempotency_key, resolves_key, created_at)
            values
              (@id, @payment, @merchant, @amount, @state, @psp, @psp_account,
               @key, @resolves_key, now())
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", change.Id.Value);
        insert.Parameters.AddWithValue("payment", view.Id.Value);
        insert.Parameters.AddWithValue("merchant", view.MerchantId.Value);
        insert.Parameters.AddWithValue("amount", amount.Minor);
        insert.Parameters.AddWithValue("state", StateText.Refund(change.State));
        insert.Parameters.AddWithValue("psp", psp);
        insert.Parameters.AddWithValue("psp_account", pspAccount);
        insert.Parameters.AddWithValue("key", (object?)idempotencyKey ?? DBNull.Value);
        insert.Parameters.AddWithValue("resolves_key", resolvesKey);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertFraudOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentId paymentId,
        MerchantId merchant,
        AttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            insert into outbox
              (id, merchant_id, aggregate_id, event_type, sequence, schema_version, payload, created_at)
            values (@id, @merchant, @aggregate, 'payment.attempt_failed', 1, 1, '{}'::jsonb, now())
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", Posting.Id(attemptId.Value, "event:payment.attempt_failed"));
        insert.Parameters.AddWithValue("merchant", merchant.Value);
        insert.Parameters.AddWithValue("aggregate", paymentId.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> ResolveAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        string accountType,
        CurrencyCode currency,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql.AccountId, connection, transaction);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("account_type", accountType);
        command.Parameters.AddWithValue("currency", currency.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id
            ? id
            : throw new InvalidOperationException($"Missing account {accountType} for {merchant}.");
    }

    private static async Task<Guid> ResolvePspAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select id from psp_accounts where merchant_id = @merchant order by id limit 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id
            ? id
            : throw new InvalidOperationException($"Merchant {merchant} has no PSP account.");
    }

    public async Task<BeginResult> CreatePaymentAsync(
        BeginAttempt command,
        CancellationToken cancellationToken)
    {
        var existing = await ReadKeyAsync(
            command.MerchantId, command.Key, command.RequestHash, cancellationToken);
        switch (existing)
        {
            case KeyLookup.Finished finished:
                return new BeginResult.Replay(finished.Response);
            case KeyLookup.HashMismatch:
                return new BeginResult.HashMismatch();
            case KeyLookup.Unresolved unresolved:
                return new BeginResult.Pending(unresolved.Payment, unresolved.Refund);
            case KeyLookup.InFlight:
                return new BeginResult.KeyInFlight();
        }

        return await _runner.RunAsync<BeginResult>(
            command.MerchantId,
            async (connection, transaction, ct) =>
            {
                if (!await InsertKeyAsync(
                        connection, transaction, command.MerchantId, command.Key,
                        command.RequestHash, command.PaymentId, null, null, ct))
                {
                    var lookup = await ReadKeyOnConnectionAsync(
                        connection, transaction, command.MerchantId, command.Key,
                        command.RequestHash, ct);
                    return lookup switch
                    {
                        KeyLookup.Finished finished => new BeginResult.Replay(finished.Response),
                        KeyLookup.HashMismatch => new BeginResult.HashMismatch(),
                        KeyLookup.Unresolved unresolved => new BeginResult.Pending(unresolved.Payment, unresolved.Refund),
                        _ => new BeginResult.KeyInFlight(),
                    };
                }

                await using var insert = new NpgsqlCommand(
                    """
                    insert into payments
                      (id, merchant_id, state, amount_minor, currency, captured_minor,
                       refunded_minor, refund_pending_minor, attempt_count, event_seq, expires_at, version)
                    values
                      (@id, @merchant, 'AWAITING_METHOD', @amount, @currency, 0, 0, 0, 0, 0, @expires, 0)
                    """,
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("id", command.PaymentId.Value);
                insert.Parameters.AddWithValue("merchant", command.MerchantId.Value);
                insert.Parameters.AddWithValue("amount", command.Amount.Minor);
                insert.Parameters.AddWithValue("currency", command.Amount.Currency.Value);
                insert.Parameters.AddWithValue("expires", command.ExpiresAt);
                await insert.ExecuteNonQueryAsync(ct);
                var view = await LoadPaymentAsync(
                    command.PaymentId, command.MerchantId, ct, connection, transaction);
                return new BeginResult.Created(view!);
            },
            cancellationToken);
    }

    public async Task<KeyLookup> WaitForKeyAsync(
        MerchantId merchant,
        string key,
        byte[] requestHash,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var lookup = await ReadKeyAsync(merchant, key, requestHash, cancellationToken);
            if (lookup is KeyLookup.Finished or KeyLookup.HashMismatch)
            {
                return lookup;
            }

            await Task.Delay(25, cancellationToken);
        }

        return new KeyLookup.InFlight();
    }

    public async Task ResolveKeyAsync(
        MerchantId merchant,
        string key,
        StoredResponse response,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update idempotency_keys
               set response_code = @code, response_body = @body::jsonb, resolved_at = now()
             where merchant_id = @merchant and key = @key
            """,
            connection);
        command.Parameters.AddWithValue("code", response.StatusCode);
        command.Parameters.AddWithValue("body", NpgsqlTypes.NpgsqlDbType.Jsonb, response.Body);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RefundRecord?> GetRefundAsync(
        MerchantId merchant,
        RefundId refundId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.id, r.payment_id, r.state, r.amount_minor, r.psp,
                   r.psp_account_id, r.created_at, p.currency, r.idempotency_key, r.resolves_key
            from refunds r
            join payments p on p.id = r.payment_id
            where r.id = @id and r.merchant_id = @merchant
            """,
            connection);
        command.Parameters.AddWithValue("id", refundId.Value);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RefundRecord(
            new RefundId(reader.GetGuid(0)),
            new PaymentId(reader.GetGuid(1)),
            merchant,
            new RefundView(
                new RefundId(reader.GetGuid(0)),
                StateText.Refund(reader.GetString(2)),
                new Money(reader.GetInt64(3), CurrencyCode.Parse(reader.GetString(7))),
                null,
                reader.GetString(4),
                reader.GetGuid(5),
                reader.GetDateTime(6),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetBoolean(9)));
    }

    public async Task<AttemptId?> GetAuthorizedAttemptAsync(
        MerchantId merchant,
        PaymentId paymentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select attempt_id
            from payment_attempts
            where payment_id = @payment and merchant_id = @merchant
              and operation in ('authorize', 'sale') and state = 'SUCCEEDED'
            order by created_at desc
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("payment", paymentId.Value);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? new AttemptId(id) : null;
    }

    public async Task<PaymentView?> GetPaymentAsync(
        MerchantId merchant,
        PaymentId paymentId,
        CancellationToken cancellationToken)
    {
        BeforeRead?.Invoke();
        return await LoadPaymentAsync(paymentId, merchant, cancellationToken);
    }

    public async Task<MerchantPspAccount?> GetPspAccountAsync(
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, psp, mode, credential_ref, rate_limit_per_s
            from psp_accounts
            where merchant_id = @merchant and disabled_at is null
            order by id
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MerchantPspAccount(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4))
            : null;
    }

    /// <summary>
    /// Persists a fraud block as a failed attempt with no PSP effect (D33). The payment is
    /// created in AWAITING_METHOD so a new key can attempt it again.
    /// </summary>
    public async Task<BeginResult> RecordFraudBlockAsync(
        BeginAttempt command,
        StoredResponse response,
        CancellationToken cancellationToken)
    {
        var merchant = command.MerchantId;
        return await _runner.RunAsync<BeginResult>(
            merchant,
            async (connection, transaction, ct) =>
            {
                var attemptId = AttemptId.New();
                if (!await InsertKeyAsync(
                        connection, transaction, merchant, command.Key, command.RequestHash,
                        command.PaymentId, attemptId, null, ct))
                {
                    var lookup = await ReadKeyOnConnectionAsync(
                        connection, transaction, merchant, command.Key, command.RequestHash, ct);
                    return lookup switch
                    {
                        KeyLookup.Finished finished => new BeginResult.Replay(finished.Response),
                        KeyLookup.HashMismatch => new BeginResult.HashMismatch(),
                        KeyLookup.Unresolved unresolved => new BeginResult.Pending(unresolved.Payment, unresolved.Refund),
                        _ => new BeginResult.KeyInFlight(),
                    };
                }

                await using (var insertPayment = new NpgsqlCommand(
                    """
                    insert into payments
                      (id, merchant_id, state, amount_minor, currency, captured_minor,
                       refunded_minor, refund_pending_minor, attempt_count, event_seq, expires_at, version)
                    values
                      (@id, @merchant, 'AWAITING_METHOD', @amount, @currency, 0, 0, 0, 1, 1, @expires, 0)
                    """,
                    connection,
                    transaction))
                {
                    insertPayment.Parameters.AddWithValue("id", command.PaymentId.Value);
                    insertPayment.Parameters.AddWithValue("merchant", merchant.Value);
                    insertPayment.Parameters.AddWithValue("amount", command.Amount.Minor);
                    insertPayment.Parameters.AddWithValue("currency", command.Amount.Currency.Value);
                    insertPayment.Parameters.AddWithValue("expires", command.ExpiresAt);
                    await insertPayment.ExecuteNonQueryAsync(ct);
                }

                var pspAccount = command.PspAccountId
                    ?? await ResolvePspAccountAsync(connection, transaction, merchant, ct);
                await InsertAttemptAsync(
                    connection, transaction, command.PaymentId, merchant,
                    new AttemptChange(attemptId, AttemptOperation.Authorize, AttemptState.Failed, "fraud_blocked"),
                    pspAccount, command.Psp, command.MethodRef, command.MethodType,
                    command.Key, false, ct);
                await InsertFraudOutboxAsync(connection, transaction, command.PaymentId, merchant, attemptId, ct);
                await ResolveKeyInTransactionAsync(
                    connection, transaction, merchant, command.Key, response, ct);
                var view = await LoadPaymentAsync(
                    command.PaymentId, merchant, ct, connection, transaction);
                return new BeginResult.Created(view!);
            },
            cancellationToken);
    }

    public async Task<BeginOperationResult> BeginOperationAsync(
        MerchantId merchant,
        PaymentId paymentId,
        string key,
        byte[] requestHash,
        PaymentCommand command,
        AttemptId? attemptId,
        RefundId? refundId,
        CancellationToken cancellationToken,
        bool resolvesKey = true)
    {
        var existing = await ReadKeyAsync(merchant, key, requestHash, cancellationToken);
        switch (existing)
        {
            case KeyLookup.Finished finished:
                return new BeginOperationResult.Replay(finished.Response);
            case KeyLookup.HashMismatch:
                return new BeginOperationResult.HashMismatch();
            case KeyLookup.Unresolved unresolved:
                return new BeginOperationResult.Pending(unresolved.Payment, unresolved.Refund);
            case KeyLookup.InFlight:
                return new BeginOperationResult.KeyInFlight();
        }

        try
        {
            return await _runner.RunAsync<BeginOperationResult>(
                merchant,
                async (connection, transaction, ct) =>
                {
                var view = await LoadPaymentAsync(
                    paymentId, merchant, ct, connection, transaction);
                if (view is null)
                {
                    return new BeginOperationResult.Rejected("not_found");
                }

                var transition = PaymentRules.Decide(view.ToSnapshot(), command, RuleVersion.V1);
                if (transition is TransitionResult.Rejected rejected)
                {
                    return new BeginOperationResult.Rejected(rejected.ReasonCode, view);
                }

                if (transition is not TransitionResult.Accepted accepted)
                {
                    return new BeginOperationResult.Rejected("invalid_state", view);
                }

                if (!await InsertKeyAsync(
                        connection, transaction, merchant, key, requestHash,
                        paymentId, attemptId, refundId, ct))
                {
                    var lookup = await ReadKeyOnConnectionAsync(
                        connection, transaction, merchant, key, requestHash, ct);
                    return lookup switch
                    {
                        KeyLookup.Finished finished => new BeginOperationResult.Replay(finished.Response),
                        KeyLookup.HashMismatch => new BeginOperationResult.HashMismatch(),
                        KeyLookup.Unresolved unresolved => new BeginOperationResult.Pending(unresolved.Payment, unresolved.Refund),
                        _ => new BeginOperationResult.KeyInFlight(),
                    };
                }

                var applied = await PersistAsync(
                    connection, transaction, view, command, accepted, null, ct, key, resolvesKey);
                    if (applied is ApplyResult.Applied done)
                    {
                        return new BeginOperationResult.Started(done.Payment);
                    }

                    await using (var delete = new NpgsqlCommand(
                        "delete from idempotency_keys where merchant_id = @merchant and key = @key",
                        connection,
                        transaction))
                    {
                        delete.Parameters.AddWithValue("merchant", merchant.Value);
                        delete.Parameters.AddWithValue("key", key);
                        await delete.ExecuteNonQueryAsync(ct);
                    }

                    return new BeginOperationResult.Rejected("already_moved", view);
                },
                cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            return new BeginOperationResult.Rejected("operation_live");
        }
    }

    private static async Task<KeyLookup> ReadKeyOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql.ReadKey, connection, transaction);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new KeyLookup.NotFound();
        }

        var storedHash = reader.GetFieldValue<byte[]>(0);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedHash, requestHash))
        {
            return new KeyLookup.HashMismatch();
        }

        if (!reader.IsDBNull(1))
        {
            return new KeyLookup.Finished(new StoredResponse(
                reader.GetInt32(1),
                reader.IsDBNull(2) ? "null" : reader.GetString(2)));
        }

        if (reader.IsDBNull(3))
        {
            return new KeyLookup.InFlight();
        }

        var paymentId = new PaymentId(reader.GetGuid(3));
        RefundId? refundId = reader.IsDBNull(5) ? null : new RefundId(reader.GetGuid(5));
        var age = reader.GetFieldValue<TimeSpan>(7);
        var refundUnknown = !reader.IsDBNull(8) && reader.GetString(8) == "UNKNOWN";
        await reader.DisposeAsync();
        var view = await LoadPaymentAsync(paymentId, merchant, cancellationToken, connection, transaction);
        if (view is null)
        {
            return new KeyLookup.InFlight();
        }

        // E27: an operation that recorded UNKNOWN has ended; a replay renders its state.
        // E2: otherwise a key younger than any request can live may belong to a running request,
        // so it answers 409 with Retry-After. An older one ended without storing its answer, such as
        // a transaction B failure after the PSP call (review H1, M4), and renders its state.
        var unknown = refundId is null
            ? view.State == PaymentState.Unknown || view.Attempt?.State == AttemptState.Unknown
            : refundUnknown;
        return unknown || age >= InFlightWindow
            ? new KeyLookup.Unresolved(view, refundId)
            : new KeyLookup.InFlight();
    }

    private async Task<PaymentView?> LoadPaymentAsync(
        PaymentId paymentId,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        return await LoadPaymentAsync(paymentId, merchant, cancellationToken, connection, null);
    }

    private static async Task<PaymentView?> LoadPaymentAsync(
        PaymentId paymentId,
        MerchantId merchant,
        CancellationToken cancellationToken,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool forUpdate = false)
    {
        PaymentState state;
        MerchantId storedMerchant;
        long amountMinor;
        string currency;
        long captured;
        long refunded;
        long pending;
        int attemptCount;
        int eventSeq;
        int version = 0;
        DateTimeOffset expiresAt = default;
        DateTimeOffset createdAt = default;
        await using (var command = new NpgsqlCommand(
            Sql.LoadPayment + (forUpdate ? " for update" : string.Empty),
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("payment_id", paymentId.Value);
            command.Parameters.AddWithValue("merchant_id", merchant.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            storedMerchant = new MerchantId(reader.GetGuid(1));
            state = StateText.Payment(reader.GetString(2));
            amountMinor = reader.GetInt64(3);
            currency = reader.GetString(4).Trim();
            captured = reader.GetInt64(5);
            refunded = reader.GetInt64(6);
            pending = reader.GetInt64(7);
            attemptCount = reader.GetInt32(8);
            eventSeq = reader.GetInt32(9);
            version = reader.GetInt32(10);
            expiresAt = reader.GetDateTime(11);
            createdAt = reader.GetDateTime(12);
        }

        AttemptView? attempt = null;
        await using (var command = new NpgsqlCommand(Sql.LoadAttempt, connection, transaction))
        {
            command.Parameters.AddWithValue("payment_id", paymentId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                attempt = new AttemptView(
                    new AttemptId(reader.GetGuid(0)),
                    StateText.Operation(reader.GetString(1)),
                    StateText.Attempt(reader.GetString(2)),
                    new Money(amountMinor, CurrencyCode.Parse(currency)),
                    reader.IsDBNull(3) ? null : new AttemptId(reader.GetGuid(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetBoolean(8));
            }
        }

        RefundView? refund = null;
        await using (var command = new NpgsqlCommand(Sql.LoadRefund, connection, transaction))
        {
            command.Parameters.AddWithValue("payment_id", paymentId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                refund = new RefundView(
                    new RefundId(reader.GetGuid(0)),
                    StateText.Refund(reader.GetString(1)),
                    new Money(reader.GetInt64(2), CurrencyCode.Parse(currency)),
                    null,
                    reader.GetString(3),
                    reader.GetGuid(4),
                    reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7));
            }
        }

        return new PaymentView(
            paymentId,
            storedMerchant,
            state,
            new Money(amountMinor, CurrencyCode.Parse(currency)),
            captured,
            refunded,
            pending,
            attemptCount,
            eventSeq,
            version,
            attempt,
            refund,
            expiresAt,
            createdAt);
    }

    private async Task<MerchantId?> MerchantForAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select merchant_id from payments where id = @id",
            connection);
        command.Parameters.AddWithValue("id", paymentId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? new MerchantId(id) : null;
    }
}
