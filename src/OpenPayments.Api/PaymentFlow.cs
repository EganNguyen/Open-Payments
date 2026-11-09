using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Transitions;

namespace OpenPayments.Api;

public sealed class PaymentFlow(
    PaymentStore store,
    IPspAdapterFactory adapters,
    IFraudCheck fraud,
    InFlightLimiter limiter,
    CanonicalLog log,
    PaymentMetrics metrics,
    RequestTelemetry telemetry)
{
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PspDeadline = TimeSpan.FromMilliseconds(2500);

    public async Task<ApiAnswer> CreatePaymentAsync(
        CreatePaymentRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(request, out var amount, out var method, out var problem))
        {
            return problem;
        }

        if (!limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            var early = await store.ReadKeyAsync(merchant, key, requestHash, cancellationToken);
            if (early is KeyLookup.Finished finishedKey)
            {
                return new ApiAnswer(finishedKey.Response.StatusCode, null, RawJson: finishedKey.Response.Body);
            }

            if (early is KeyLookup.HashMismatch)
            {
                return ApiAnswer.Problem(422, "The key was used for another request.", "idempotency_key_reused");
            }

            var account = await store.GetPspAccountAsync(merchant, cancellationToken);
            var supportsSale = account is not null
                && adapters.Capabilities(new PspAccount(
                    account.Id, merchant, account.Psp, account.Mode, account.CredentialRef)).SupportsSale;

            if (early is KeyLookup.Unresolved unresolved)
            {
                // M1: a two-call auto-capture whose capture didn't finish runs it again. Its own key
                // replays a capture that already happened, so the PSP is never asked twice.
                if (request.CaptureMode == "auto" && !supportsSale
                    && unresolved.Payment.State == PaymentState.Authorized)
                {
                    return await CaptureForCreateAsync(unresolved.Payment.Id, unresolved.Payment.Amount, merchant, key, cancellationToken);
                }

                return await PendingAsync(unresolved.Payment, unresolved.Refund, merchant);
            }

            if (early is KeyLookup.InFlight)
            {
                return ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", retryAfter: 1);
            }

            var paymentId = new PaymentId(Guid.NewGuid());
            var operation = request.CaptureMode == "auto" && supportsSale
                ? AttemptOperation.Sale
                : AttemptOperation.Authorize;
            var command = new BeginAttempt(
                merchant,
                key,
                requestHash,
                paymentId,
                amount,
                operation,
                CreatePayment: true,
                DateTimeOffset.UtcNow.Add(PaymentWindow),
                MethodRef: method?.Ref ?? string.Empty,
                MethodType: method?.Type ?? string.Empty);

            if (method is null)
            {
                var created = await store.CreatePaymentAsync(command, cancellationToken);
                return await FromBeginAsync(
                    created, merchant, key, null, requestHash, cancellationToken, countCompleted: true);
            }

            var checkedFraud = await fraud.CheckAsync(merchant, paymentId, amount, cancellationToken);
            if (checkedFraud is FraudDecision.Block block)
            {
                var body = JsonSerializer.Serialize(
                    new { id = paymentId.Value, state = "AWAITING_METHOD", code = block.Code },
                    ApiJson.Options);
                await store.RecordFraudBlockAsync(
                    command, new StoredResponse(402, body), cancellationToken);
                return new ApiAnswer(402, null, RawJson: body);
            }

            var begin = await store.BeginAttemptAsync(command, cancellationToken);
            if (request.CaptureMode == "auto" && !supportsSale && begin is BeginResult.Started started)
            {
                // M1: the create key stores the answer the client gets, which is the capture's.
                var authorized = await ExecuteAttemptAsync(
                    started, merchant, key, method, cancellationToken, countCompleted: false, resolveKey: false);
                if (authorized.StatusCode != 200)
                {
                    if (authorized.StatusCode != 202)
                    {
                        await ResolveWithAsync(merchant, key, authorized);
                    }

                    return authorized;
                }

                return await CaptureForCreateAsync(paymentId, amount, merchant, key, cancellationToken);
            }

            return await FromBeginAsync(
                begin, merchant, key, method, requestHash, cancellationToken, countCompleted: true);
        }
        catch (CommitOutcomeUnknownException exception)
        {
            log.Record(Entry(merchant, null, null, null, "UNKNOWN", "transaction_a_unknown", "authorize"));
            return ApiAnswer.Problem(503, "The outcome is not known yet.", exception.Reason, retryAfter: 1);
        }
        catch (BusyException exception)
        {
            log.Record(Entry(merchant, null, null, null, "BUSY", exception.Reason, "authorize"));
            return ApiAnswer.Problem(503, "The database is busy.", "busy", retryAfter: 1);
        }
        catch (Npgsql.NpgsqlException)
        {
            // N1: a database failure that escaped the attempt's own handling. The attempt's PSP
            // call, if any, answers through ExecuteAttemptAsync, so this is before it.
            log.Record(Entry(merchant, null, null, null, "BUSY", "database_unavailable", "authorize"));
            return ApiAnswer.Problem(503, "The database is unavailable.", "database_unavailable", retryAfter: 1);
        }
        finally
        {
            limiter.Exit();
        }
    }

    public async Task<ApiAnswer> CreateAttemptAsync(
        PaymentId paymentId,
        CreateAttemptRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (request.Method is null
            || string.IsNullOrWhiteSpace(request.Method.Type)
            || string.IsNullOrWhiteSpace(request.Method.Ref))
        {
            return ApiAnswer.Problem(422, "A payment method is required.", "invalid_method");
        }

        if (!limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            var early = await store.ReadKeyAsync(merchant, key, requestHash, cancellationToken);
            if (early is KeyLookup.Finished finishedKey)
            {
                return new ApiAnswer(finishedKey.Response.StatusCode, null, RawJson: finishedKey.Response.Body);
            }

            if (early is KeyLookup.HashMismatch)
            {
                return ApiAnswer.Problem(422, "The key was used for another request.", "idempotency_key_reused");
            }

            if (early is KeyLookup.Unresolved unresolved)
            {
                return await PendingAsync(unresolved.Payment, unresolved.Refund, merchant);
            }

            if (early is KeyLookup.InFlight)
            {
                return ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", retryAfter: 1);
            }

            var existing = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
            if (existing is null)
            {
                return ApiAnswer.Problem(404, "Payment not found.", "not_found");
            }

            var method = new PaymentMethodRef(request.Method.Type, request.Method.Ref);
            var command = new BeginAttempt(
                merchant,
                key,
                requestHash,
                paymentId,
                existing.Amount,
                AttemptOperation.Authorize,
                CreatePayment: false,
                DateTimeOffset.UtcNow.Add(PaymentWindow),
                MethodRef: method.Ref,
                MethodType: method.Type);

            var checkedFraud = await fraud.CheckAsync(merchant, paymentId, existing.Amount, cancellationToken);
            if (checkedFraud is FraudDecision.Block)
            {
                return ApiAnswer.Problem(402, "The attempt was blocked.", "fraud_blocked");
            }

            var begin = await store.BeginAttemptAsync(command, cancellationToken);
            return await FromBeginAsync(
                begin, merchant, key, method, requestHash, cancellationToken, countCompleted: true);
        }
        catch (CommitOutcomeUnknownException exception)
        {
            log.Record(Entry(merchant, paymentId, null, null, "UNKNOWN", "transaction_a_unknown", "attempt"));
            return ApiAnswer.Problem(503, "The outcome is not known yet.", exception.Reason, retryAfter: 1);
        }
        catch (BusyException exception)
        {
            // M2, D1: a lock timeout here is before any PSP call, so 503 with Retry-After.
            log.Record(Entry(merchant, paymentId, null, null, "BUSY", exception.Reason, "attempt"));
            return ApiAnswer.Problem(503, "The database is busy.", "busy", retryAfter: 1);
        }
        catch (Npgsql.NpgsqlException)
        {
            log.Record(Entry(merchant, paymentId, null, null, "BUSY", "database_unavailable", "attempt"));
            return ApiAnswer.Problem(503, "The database is unavailable.", "database_unavailable", retryAfter: 1);
        }
        finally
        {
            limiter.Exit();
        }
    }

    public async Task<ApiAnswer> GetPaymentAsync(
        PaymentId paymentId,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        var view = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        return view is null
            ? ApiAnswer.Problem(404, "Payment not found.", "not_found")
            : ApiAnswer.Payment(view);
    }

    public async Task<ApiAnswer> CancelAsync(
        PaymentId paymentId,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        // L5, D34: every money route counts against the request limit.
        if (!limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            return await CancelCoreAsync(paymentId, merchant, key, requestHash, cancellationToken);
        }
        finally
        {
            limiter.Exit();
        }
    }

    private async Task<ApiAnswer> CancelCoreAsync(
        PaymentId paymentId,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (existing is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        var (result, refused) = await BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Cancel(DateTimeOffset.UtcNow),
            attemptId: null,
            refundId: null,
            "cancel",
            cancellationToken);
        if (result is null)
        {
            return refused!;
        }

        if (result is BeginOperationResult.Started started)
        {
            var body = JsonSerializer.Serialize(PaymentBody.From(started.Payment), ApiJson.Options);
            // M4: the state has committed, so a disconnected client mustn't stop the answer's write.
            await store.ResolveKeyAsync(merchant, key, new StoredResponse(200, body), CancellationToken.None);
            return new ApiAnswer(200, null, RawJson: body);
        }

        if (result is BeginOperationResult.Pending pending)
        {
            return await PendingAsync(pending.Payment, pending.Refund, merchant);
        }

        return result switch
        {
            BeginOperationResult.Replay replay => new ApiAnswer(
                replay.Response.StatusCode, null, RawJson: replay.Response.Body),
            BeginOperationResult.KeyInFlight => ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", 1),
            BeginOperationResult.HashMismatch => ApiAnswer.Problem(422, "The key was used for another request.", "idempotency_key_reused"),
            BeginOperationResult.Rejected rejected => ApiAnswer.Problem(409, "The payment cannot be canceled.", rejected.ReasonCode),
            _ => ApiAnswer.Problem(409, "The payment cannot be canceled.", "invalid_state"),
        };
    }

    public async Task<ApiAnswer> CaptureAsync(
        PaymentId paymentId,
        CaptureRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken,
        bool countCompleted = false,
        bool enterLimiter = true)
    {
        // L5, D34: every money route counts against the request limit. The two-call create
        // already holds a permit for its capture.
        if (enterLimiter && !limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            return await CaptureCoreAsync(paymentId, request, merchant, key, requestHash, cancellationToken, countCompleted);
        }
        finally
        {
            if (enterLimiter)
            {
                limiter.Exit();
            }
        }
    }

    private async Task<ApiAnswer> CaptureCoreAsync(
        PaymentId paymentId,
        CaptureRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken,
        bool countCompleted)
    {
        var view = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (view is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        if (request.AmountMinor != view.Amount.Minor)
        {
            return await ReplayAsync(merchant, key, requestHash, cancellationToken)
                ?? ApiAnswer.Problem(422, "The amount must equal the authorized amount.", "capture_amount_mismatch");
        }

        var attemptId = new AttemptId(Guid.NewGuid());
        var (begin, refused) = await BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Capture(attemptId, view.Amount, DateTimeOffset.UtcNow),
            attemptId,
            refundId: null,
            "capture",
            cancellationToken);
        if (begin is null)
        {
            return refused!;
        }

        if (begin is not BeginOperationResult.Started started)
        {
            return await FromOperationAsync(begin, merchant);
        }

        var attempt = started.Payment.Attempt ?? throw new InvalidOperationException("Capture has no attempt.");
        var pspAccount = new PspAccount(attempt.PspAccountId, merchant, attempt.Psp, "live", string.Empty);
        var adapter = adapters.For(pspAccount);
        PspResult result;
        try
        {
            using var pspWait = telemetry.MeasurePspWait();
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.CaptureAsync(attempt.Id, view.Amount, deadline.Token);
        }
#pragma warning disable CA1031 // M8, D27: once the request may have reached the PSP, every failure is inconclusive.
        catch (Exception)
#pragma warning restore CA1031
        {
            return await AfterPspAsync(paymentId, null, merchant, attempt.Id, "capture", async () =>
            {
                var timedOut = await store.ApplyAsync(
                    paymentId,
                    new PaymentCommand.CaptureAnswer(
                        attempt.Id, false, null, "timeout", TimedOut: true, DateTimeOffset.UtcNow),
                    null,
                    CancellationToken.None);
                return await FromAppliedAsync(timedOut, merchant, key, 202, 0, timedOut: true, countCompleted: false);
            });
        }

        var answer = result switch
        {
            PspResult.Approved => new PaymentCommand.CaptureAnswer(
                attempt.Id, true, view.Amount, null, false, DateTimeOffset.UtcNow),
            PspResult.Declined declined => new PaymentCommand.CaptureAnswer(
                attempt.Id, false, null, declined.FailureCode, false, DateTimeOffset.UtcNow),
            _ => new PaymentCommand.CaptureAnswer(
                attempt.Id, false, null, "action_required", false, DateTimeOffset.UtcNow),
        };
        var status = result is PspResult.Declined ? 402 : 200;
        return await AfterPspAsync(paymentId, null, merchant, attempt.Id, "capture", async () =>
        {
            var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
            return await FromAppliedAsync(applied, merchant, key, status, 0, timedOut: false, countCompleted: countCompleted);
        });
    }

    public async Task<ApiAnswer> VoidAsync(
        PaymentId paymentId,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        // L5, D34: every money route counts against the request limit.
        if (!limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            return await VoidCoreAsync(paymentId, merchant, key, requestHash, cancellationToken);
        }
        finally
        {
            limiter.Exit();
        }
    }

    private async Task<ApiAnswer> VoidCoreAsync(
        PaymentId paymentId,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var view = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (view is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        var authorized = await store.GetAuthorizedAttemptAsync(merchant, paymentId, cancellationToken);
        if (authorized is null)
        {
            // D31: the first request may have changed the state this checks, so a replay gets its
            // answer, not this refusal. Read only on refusal, so the normal path costs nothing.
            return await ReplayAsync(merchant, key, requestHash, cancellationToken)
                ?? ApiAnswer.Problem(422, "The payment is not authorized.", "invalid_state");
        }

        var attemptId = new AttemptId(Guid.NewGuid());
        var (begin, refused) = await BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Void(attemptId, authorized.Value, DateTimeOffset.UtcNow),
            attemptId,
            refundId: null,
            "void",
            cancellationToken);
        if (begin is null)
        {
            return refused!;
        }

        if (begin is not BeginOperationResult.Started started)
        {
            return await FromOperationAsync(begin, merchant);
        }

        var attempt = started.Payment.Attempt ?? throw new InvalidOperationException("Void has no attempt.");
        var pspAccount = new PspAccount(attempt.PspAccountId, merchant, attempt.Psp, "live", string.Empty);
        var adapter = adapters.For(pspAccount);
        PspResult result;
        try
        {
            using var pspWait = telemetry.MeasurePspWait();
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.VoidAsync(attempt.Id, authorized.Value, deadline.Token);
        }
#pragma warning disable CA1031 // M8, D27: once the request may have reached the PSP, every failure is inconclusive.
        catch (Exception)
#pragma warning restore CA1031
        {
            return await AfterPspAsync(paymentId, null, merchant, attempt.Id, "void", async () =>
            {
                var timedOut = await store.ApplyAsync(
                    paymentId,
                    new PaymentCommand.VoidAnswer(attempt.Id, false, TimedOut: true, "timeout", DateTimeOffset.UtcNow),
                    null,
                    CancellationToken.None);
                return await FromAppliedAsync(timedOut, merchant, key, 202, 0, timedOut: true, countCompleted: false);
            });
        }

        var answer = result is PspResult.Approved
            ? new PaymentCommand.VoidAnswer(attempt.Id, true, false, null, DateTimeOffset.UtcNow)
            : new PaymentCommand.VoidAnswer(
                attempt.Id, false, false,
                (result as PspResult.Declined)?.FailureCode ?? "declined",
                DateTimeOffset.UtcNow);
        var status = result is PspResult.Approved ? 200 : 402;
        return await AfterPspAsync(paymentId, null, merchant, attempt.Id, "void", async () =>
        {
            var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
            return await FromAppliedAsync(applied, merchant, key, status, 0, timedOut: false, countCompleted: false);
        });
    }

    public async Task<ApiAnswer> RefundAsync(
        PaymentId paymentId,
        RefundRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        // L5, D34: every money route counts against the request limit.
        if (!limiter.TryEnter())
        {
            return ApiAnswer.Problem(503, "The service is busy.", "busy", retryAfter: 1);
        }

        try
        {
            return await RefundCoreAsync(paymentId, request, merchant, key, requestHash, cancellationToken);
        }
        finally
        {
            limiter.Exit();
        }
    }

    private async Task<ApiAnswer> RefundCoreAsync(
        PaymentId paymentId,
        RefundRequest request,
        MerchantId merchant,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var view = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (view is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        if (request.AmountMinor <= 0 || request.AmountMinor > view.Amount.Minor - view.RefundedMinor - view.RefundPendingMinor)
        {
            // D31: a replay of a refund that reserved this amount gets its answer, not this refusal.
            return await ReplayAsync(merchant, key, requestHash, cancellationToken)
                ?? ApiAnswer.Problem(422, "The refund amount is invalid.", "refund_exceeds_captured");
        }

        var refundId = RefundId.New();
        var amount = new Money(request.AmountMinor, view.Amount.Currency);
        var (begin, refused) = await BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.RefundReserve(refundId, amount, DateTimeOffset.UtcNow),
            attemptId: null,
            refundId,
            "refund",
            cancellationToken);
        if (begin is null)
        {
            return refused!;
        }

        if (begin is not BeginOperationResult.Started started)
        {
            return await FromOperationAsync(begin, merchant);
        }

        var attempt = started.Payment.Attempt;
        if (attempt is null)
        {
            return ApiAnswer.Problem(422, "The payment has no attempt.", "invalid_state");
        }

        var adapter = adapters.For(new PspAccount(
            attempt.PspAccountId, merchant, attempt.Psp, "live", string.Empty));
        PspResult result;
        try
        {
            using var pspWait = telemetry.MeasurePspWait();
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.RefundAsync(refundId, amount, deadline.Token);
        }
#pragma warning disable CA1031 // M8, D27: once the request may have reached the PSP, every failure is inconclusive.
        catch (Exception)
#pragma warning restore CA1031
        {
            return await AfterPspAsync(paymentId, refundId, merchant, null, "refund", async () =>
            {
                var timedOut = await store.ApplyAsync(
                    paymentId,
                    new PaymentCommand.RefundTimeout(refundId, DateTimeOffset.UtcNow),
                    null,
                    CancellationToken.None);
                return await FromRefundAppliedAsync(timedOut, merchant, key, 202, refundId);
            });
        }

        PaymentCommand answer = result switch
        {
            PspResult.Approved => new PaymentCommand.RefundConfirm(refundId, amount, DateTimeOffset.UtcNow),
            PspResult.Declined declined => new PaymentCommand.RefundRelease(
                refundId, ProvenNotCharged: true, declined.FailureCode, DateTimeOffset.UtcNow),
            _ => new PaymentCommand.RefundTimeout(refundId, DateTimeOffset.UtcNow),
        };
        var status = result is PspResult.Approved ? 200 : 402;
        return await AfterPspAsync(paymentId, refundId, merchant, null, "refund", async () =>
        {
            var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
            return await FromRefundAppliedAsync(applied, merchant, key, status, refundId);
        });
    }

    public async Task<ApiAnswer> GetRefundAsync(
        RefundId refundId,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        var refund = await store.GetRefundAsync(merchant, refundId, cancellationToken);
        return refund is null
            ? ApiAnswer.Problem(404, "Refund not found.", "not_found")
            : new ApiAnswer(200, RefundBody.From(refund));
    }

    private async Task<ApiAnswer> FromRefundAppliedAsync(
        ApplyResult result,
        MerchantId merchant,
        string key,
        int status,
        RefundId refundId)
    {
        if (result is not ApplyResult.Applied applied)
        {
            return Refusal(new BeginOperationResult.Rejected("invalid_state"));
        }

        if (applied.Payment.Refund is null)
        {
            return Refusal(new BeginOperationResult.Rejected("invalid_state", applied.Payment));
        }

        var refund = applied.Payment.Refund;
        var body = JsonSerializer.Serialize(
            RefundBody.From(refund, applied.Payment.Id), ApiJson.Options);
        // M3: an unknown outcome leaves the key unresolved, so it can't expire (D19) and a replay
        // shows the refund as it is then (M4). M4: written without the request's token.
        if (status != 202)
        {
            await store.ResolveKeyAsync(
                merchant, key, new StoredResponse(status, body), CancellationToken.None);
        }

        return new ApiAnswer(status, null, RawJson: body);
    }

    private async Task<ApiAnswer> FromOperationAsync(BeginOperationResult result, MerchantId merchant) =>
        result is BeginOperationResult.Pending pending
            ? await PendingAsync(pending.Payment, pending.Refund, merchant)
            : Refusal(result);

    private static ApiAnswer Refusal(BeginOperationResult result) => result switch
    {
        BeginOperationResult.Replay replay => new ApiAnswer(
            replay.Response.StatusCode, null, RawJson: replay.Response.Body),
        BeginOperationResult.KeyInFlight => ApiAnswer.Problem(
            409, "A request is in flight.", "request_in_flight", retryAfter: 1),
        BeginOperationResult.HashMismatch => ApiAnswer.Problem(
            422, "The key was used for another request.", "idempotency_key_reused"),
        BeginOperationResult.Rejected rejected
            when rejected.ReasonCode is "refund_live" or "already_moved" or "operation_live" =>
            ApiAnswer.Problem(
                409, "Another operation is live; retry shortly.", "operation_live", retryAfter: 1),
        BeginOperationResult.Rejected rejected => ApiAnswer.Problem(
            422, "The request was refused.", rejected.ReasonCode),
        _ => ApiAnswer.Problem(409, "The operation is already live.", "operation_live", retryAfter: 1),
    };

    private async Task<ApiAnswer> FromBeginAsync(
        BeginResult result,
        MerchantId merchant,
        string key,
        PaymentMethodRef? method,
        byte[] requestHash,
        CancellationToken cancellationToken,
        bool countCompleted)
    {
        switch (result)
        {
            case BeginResult.Replay replay:
                return new ApiAnswer(replay.Response.StatusCode, null, RawJson: replay.Response.Body);
            case BeginResult.KeyInFlight:
                var finished = await store.WaitForKeyAsync(
                    merchant, key, requestHash, TimeSpan.FromSeconds(1), cancellationToken);
                return finished is KeyLookup.Finished finishedKey
                    ? new ApiAnswer(finishedKey.Response.StatusCode, null, RawJson: finishedKey.Response.Body)
                    : ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", retryAfter: 1);
            case BeginResult.HashMismatch:
                return ApiAnswer.Problem(422, "The key was used for another request.", "idempotency_key_reused");
            case BeginResult.Pending pending:
                return await PendingAsync(pending.Payment, pending.Refund, merchant);
            case BeginResult.LiveAttemptExists:
            case BeginResult.CapExceeded:
                return ApiAnswer.Problem(409, "An attempt is already live.", "attempt_live", retryAfter: 1);
            case BeginResult.Rejected rejected:
                return rejected.ReasonCode == "not_found"
                    ? ApiAnswer.Problem(404, "Payment not found.", "not_found")
                    : ApiAnswer.Problem(422, "The request was refused.", rejected.ReasonCode);
            case BeginResult.Created created:
                var body = JsonSerializer.Serialize(PaymentBody.From(created.Payment), ApiJson.Options);
                await store.ResolveKeyAsync(merchant, key, new StoredResponse(200, body), cancellationToken);
                return new ApiAnswer(200, null, RawJson: body);
            case BeginResult.Started started:
                return await ExecuteAttemptAsync(
                    started,
                    merchant,
                    key,
                    method ?? new PaymentMethodRef("card", "tok_test"),
                    cancellationToken,
                    countCompleted);
            default:
                return ApiAnswer.Problem(500, "Unhandled answer.", "internal");
        }
    }

    private async Task<ApiAnswer> ExecuteAttemptAsync(
        BeginResult.Started started,
        MerchantId merchant,
        string key,
        PaymentMethodRef method,
        CancellationToken cancellationToken,
        bool countCompleted,
        bool resolveKey = true)
    {
        var view = started.Payment;
        var attempt = view.Attempt ?? throw new InvalidOperationException("A started attempt has no row.");
        var account = new PspAccount(
            attempt.PspAccountId,
            merchant,
            attempt.Psp,
            "live",
            attempt.Id.Value.ToString("D"));
        var adapter = adapters.For(account);
        var operation = attempt.Operation;
        var pspWatch = Stopwatch.StartNew();
        PspResult result;
        try
        {
            using var pspWait = telemetry.MeasurePspWait();
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = operation == AttemptOperation.Sale
                ? await adapter.SaleAsync(attempt.Id, view.Amount, method, deadline.Token)
                : await adapter.AuthorizeAsync(attempt.Id, view.Amount, method, deadline.Token);
        }
#pragma warning disable CA1031 // M8, D27: once the request may have reached the PSP, every failure is inconclusive.
        catch (Exception)
#pragma warning restore CA1031
        {
            pspWatch.Stop();
            // H3: recording the timeout is after the PSP call, so its failure is an unknown
            // outcome (202), never the retryable 503 of transaction A.
            return await AfterPspAsync(view.Id, null, merchant, attempt.Id, "authorize", async () =>
            {
                var timedOut = await store.ApplyAsync(
                    view.Id,
                    new PaymentCommand.Timeout(attempt.Id, operation, DateTimeOffset.UtcNow),
                    null,
                    CancellationToken.None);
                return await FromAppliedAsync(
                    timedOut, merchant, key, 202, pspWatch.ElapsedMilliseconds, timedOut: true,
                    countCompleted: false, resolveKey);
            });
        }

        pspWatch.Stop();
        PaymentCommand answer = result switch
        {
            PspResult.Approved approved => new PaymentCommand.PspAnswer(
                attempt.Id, operation, PspAnswerOutcome.Approved, approved.Amount ?? view.Amount,
                null, null, DateTimeOffset.UtcNow),
            PspResult.Declined declined => new PaymentCommand.PspAnswer(
                attempt.Id, operation, PspAnswerOutcome.Declined, null,
                declined.FailureCode, null, DateTimeOffset.UtcNow),
            PspResult.RequiresAction action => new PaymentCommand.PspAnswer(
                attempt.Id, operation, PspAnswerOutcome.RequiresAction, view.Amount,
                null, action.ActionType, DateTimeOffset.UtcNow),
            // M8: a result this code doesn't know is no answer at all: record it as a timeout.
            _ => new PaymentCommand.Timeout(attempt.Id, operation, DateTimeOffset.UtcNow),
        };
        if (answer is PaymentCommand.Timeout unknownResult)
        {
            return await AfterPspAsync(view.Id, null, merchant, attempt.Id, "authorize", async () =>
            {
                var timedOut = await store.ApplyAsync(view.Id, unknownResult, null, CancellationToken.None);
                return await FromAppliedAsync(
                    timedOut, merchant, key, 202, pspWatch.ElapsedMilliseconds, timedOut: true,
                    countCompleted: false, resolveKey);
            });
        }

        var status = result is PspResult.Declined ? 402 : 200;
        return await AfterPspAsync(view.Id, null, merchant, attempt.Id, "authorize", async () =>
        {
            var applied = await store.ApplyAsync(view.Id, answer, null, CancellationToken.None);
            return await FromAppliedAsync(
                applied, merchant, key, status, pspWatch.ElapsedMilliseconds, false, countCompleted, resolveKey);
        });
    }

    private async Task<ApiAnswer> FromAppliedAsync(
        ApplyResult result,
        MerchantId merchant,
        string key,
        int status,
        long pspWaitMs,
        bool timedOut,
        bool countCompleted,
        bool resolveKey = true)
    {
        switch (result)
        {
            case ApplyResult.Rejected rejected:
                return ApiAnswer.Problem(409, "The transition was refused.", rejected.ReasonCode);
            case ApplyResult.AlreadyMoved already:
                return ApiAnswer.Payment(already.Payment);
            case ApplyResult.Applied applied:
                var finalStatus = timedOut ? 202 : status;
                var body = JsonSerializer.Serialize(PaymentBody.From(applied.Payment), ApiJson.Options);
                // M3: an unknown outcome (202) leaves the key unresolved, so it can't expire under
                // D19 and a replay renders the state as it is then (M4).
                if (resolveKey && finalStatus != 202)
                {
                    await store.ResolveKeyAsync(
                        merchant, key, new StoredResponse(finalStatus, body), CancellationToken.None);
                }
                if (countCompleted && CompletedOutcome(applied.Payment) is { } outcome)
                {
                    metrics.Completed(outcome);
                }

                log.Record(Entry(
                    merchant,
                    applied.Payment.Id,
                    applied.Payment.Attempt?.Id,
                    null,
                    PaymentStateText.Payment(applied.Payment.State),
                    timedOut ? "timeout" : "admitted",
                    "authorize") with { PspWaitMs = pspWaitMs });
                return new ApiAnswer(finalStatus, null, RawJson: body);
            default:
                return ApiAnswer.Problem(500, "Unhandled answer.", "internal");
        }
    }

    /// <summary>
    /// Transaction B, after the PSP answered. Any failure here, a lock timeout, exhausted retries,
    /// an unknown commit or a dropped connection, can't undo what the PSP did, so the outcome is
    /// unknown (D28): 202 with the state as stored, and the key left unresolved, so a same-key
    /// retry shows the state as it is then (D1, H1).
    /// </summary>
    private async Task<ApiAnswer> AfterPspAsync(
        PaymentId paymentId,
        RefundId? refundId,
        MerchantId merchant,
        AttemptId? attemptId,
        string requestClass,
        Func<Task<ApiAnswer>> apply)
    {
        try
        {
            return await apply();
        }
#pragma warning disable CA1031 // After the PSP answered, every failure is an unknown outcome, never a 500.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var reason = exception switch
            {
                BusyException busy => busy.Reason,
                CommitOutcomeUnknownException unknown => unknown.Reason,
                _ => exception.GetType().Name,
            };
            log.Record(Entry(merchant, paymentId, attemptId, null, "UNKNOWN", "transaction_b_" + reason, requestClass));
            try
            {
                var current = await store.GetPaymentAsync(merchant, paymentId, CancellationToken.None);
                if (current is not null)
                {
                    return await PendingAsync(current, refundId, merchant);
                }
            }
#pragma warning disable CA1031 // N3: the database may still be down; the answer stays 202.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            return UnknownFallback(paymentId, refundId);
        }
    }

    /// <summary>N3: the 202 body when the stored state can't be read, from the IDs in hand.</summary>
    private static ApiAnswer UnknownFallback(PaymentId paymentId, RefundId? refundId) =>
        new(202, null, RawJson: refundId is { } refund
            ? JsonSerializer.Serialize(new { id = refund.Value, payment_id = paymentId.Value, state = "UNKNOWN" }, ApiJson.Options)
            : JsonSerializer.Serialize(new { id = paymentId.Value, state = "UNKNOWN" }, ApiJson.Options));

    /// <summary>
    /// The answer for a key already used on this route: the stored answer, the current state of
    /// an unresolved operation, 409 while one may still be running (E2), or 422 for another
    /// request's key. Null when the key is new.
    /// </summary>
    private async Task<ApiAnswer?> ReplayAsync(MerchantId merchant, string key, byte[] requestHash, CancellationToken cancellationToken) =>
        await store.ReadKeyAsync(merchant, key, requestHash, cancellationToken) switch
        {
            KeyLookup.Finished finished => new ApiAnswer(finished.Response.StatusCode, null, RawJson: finished.Response.Body),
            KeyLookup.HashMismatch => ApiAnswer.Problem(422, "The key was used for another request.", "idempotency_key_reused"),
            KeyLookup.Unresolved unresolved => await PendingAsync(unresolved.Payment, unresolved.Refund, merchant),
            KeyLookup.InFlight => ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", retryAfter: 1),
            _ => null,
        };

    /// <summary>The current state of an operation whose outcome isn't stored yet: 202 with the
    /// refund when the key belongs to one, otherwise the payment (D31, E27).</summary>
    private async Task<ApiAnswer> PendingAsync(PaymentView payment, RefundId? refundId, MerchantId merchant)
    {
        if (refundId is { } id
            && await store.GetRefundAsync(merchant, id, CancellationToken.None) is { } refund)
        {
            return new ApiAnswer(202, null, RawJson: JsonSerializer.Serialize(RefundBody.From(refund), ApiJson.Options));
        }

        var current = await store.GetPaymentAsync(merchant, payment.Id, CancellationToken.None) ?? payment;
        return new ApiAnswer(202, null, RawJson: JsonSerializer.Serialize(PaymentBody.From(current), ApiJson.Options));
    }

    /// <summary>The capture half of a two-call auto-capture (M1). It runs under an internal key no
    /// client can send, since headers can't carry a control character, and the create key stores
    /// the capture's final answer, which is what the client was told.</summary>
    private async Task<ApiAnswer> CaptureForCreateAsync(
        PaymentId paymentId,
        Money amount,
        MerchantId merchant,
        string key,
        CancellationToken cancellationToken)
    {
        var captured = await CaptureAsync(
            paymentId,
            new CaptureRequest(amount.Minor),
            merchant,
            "\u0001capture:" + key,
            RequestHash("POST", "/internal/capture", ReadOnlySpan<byte>.Empty),
            cancellationToken,
            countCompleted: true,
            enterLimiter: false);
        if (captured.StatusCode == 503)
        {
            // M9, D28: the authorization was already sent, so a capture that couldn't start isn't
            // "nothing happened". Answer 202 with the state; a same-key replay retries the capture.
            try
            {
                if (await store.GetPaymentAsync(merchant, paymentId, CancellationToken.None) is { } current)
                {
                    return await PendingAsync(current, null, merchant);
                }
            }
#pragma warning disable CA1031 // N3: the database may still be down; the answer stays 202.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            return UnknownFallback(paymentId, null);
        }

        if (captured.StatusCode is 200 or 402)
        {
            await ResolveWithAsync(merchant, key, captured);
        }

        return captured;
    }

    /// <summary>Stores the create key's answer on the two-call path. It runs after a PSP call, so a
    /// failure here mustn't change the answer the client gets (M9): the key stays unresolved, and
    /// a replay renders the state.</summary>
    private async Task ResolveWithAsync(MerchantId merchant, string key, ApiAnswer answer)
    {
        var body = answer.RawJson ?? JsonSerializer.Serialize(answer.Body, ApiJson.Options);
        try
        {
            await store.ResolveKeyAsync(merchant, key, new StoredResponse(answer.StatusCode, body), CancellationToken.None);
        }
#pragma warning disable CA1031 // M9: see above.
        catch (Exception)
#pragma warning restore CA1031
        {
            log.Record(Entry(merchant, null, null, null, "UNKNOWN", "key_write_failed", "authorize"));
        }
    }

    private static string? CompletedOutcome(PaymentView payment) => payment.State switch
    {
        PaymentState.Captured => "captured",
        PaymentState.Authorized => "authorized",
        PaymentState.AwaitingMethod when payment.Attempt is
            { State: AttemptState.Failed, FailureCode: not "fraud_blocked" and not "timeout" } => "declined",
        _ => null,
    };

    /// <summary>
    /// Transaction A of a money operation. A lock timeout, exhausted retries or an unknown
    /// commit here happens before any PSP call, so it answers 503 with Retry-After, as the
    /// create route does. Exactly one of Result and Refused is set. Failures after the PSP call
    /// are handled by each route, not here.
    /// </summary>
    private async Task<(BeginOperationResult? Result, ApiAnswer? Refused)> BeginOperationAsync(
        MerchantId merchant,
        PaymentId paymentId,
        string key,
        byte[] requestHash,
        PaymentCommand command,
        AttemptId? attemptId,
        RefundId? refundId,
        string requestClass,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await store.BeginOperationAsync(
                merchant, paymentId, key, requestHash, command, attemptId, refundId, cancellationToken);
            return (result, null);
        }
        catch (CommitOutcomeUnknownException exception)
        {
            log.Record(Entry(merchant, paymentId, attemptId, null, "UNKNOWN", "transaction_a_unknown", requestClass));
            return (null, ApiAnswer.Problem(503, "The outcome is not known yet.", exception.Reason, retryAfter: 1));
        }
        catch (BusyException exception)
        {
            log.Record(Entry(merchant, paymentId, attemptId, null, "BUSY", exception.Reason, requestClass));
            return (null, ApiAnswer.Problem(503, "The database is busy.", "busy", retryAfter: 1));
        }
        catch (Npgsql.NpgsqlException)
        {
            // N1: before any PSP call the transaction rolled back, so it is safe to retry.
            log.Record(Entry(merchant, paymentId, attemptId, null, "BUSY", "database_unavailable", requestClass));
            return (null, ApiAnswer.Problem(503, "The database is unavailable.", "database_unavailable", retryAfter: 1));
        }
    }

    private static CanonicalEntry Entry(
        MerchantId merchant,
        PaymentId? payment,
        AttemptId? attempt,
        string? from,
        string to,
        string limiter,
        string requestClass) =>
        new(
            merchant.ToString(),
            payment?.Value.ToString("D"),
            attempt?.Value.ToString("D"),
            from,
            to,
            limiter,
            requestClass,
            "mock",
            "closed",
            0,
            0,
            0,
            "v1");

    public static byte[] RequestHash(string method, string path, ReadOnlySpan<byte> body)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(method + "\n" + path + "\n");
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer.AsSpan(prefix.Length));
        return SHA256.HashData(buffer);
    }

    private static bool TryValidate(
        CreatePaymentRequest request,
        out Money amount,
        out PaymentMethodRef? method,
        out ApiAnswer problem)
    {
        amount = default;
        method = null;
        problem = ApiAnswer.Problem(422, "The request is invalid.", "invalid_request");
        if (request.AmountMinor <= 0 || request.AmountMinor > Money.MaximumMinor)
        {
            problem = ApiAnswer.Problem(422, "The amount is invalid.", "invalid_amount");
            return false;
        }

        if (!CurrencyCode.TryParse(request.Currency, out var currency))
        {
            problem = ApiAnswer.Problem(422, "The currency is invalid.", "invalid_currency");
            return false;
        }

        if (request.CaptureMode is not ("auto" or "manual"))
        {
            problem = ApiAnswer.Problem(422, "The capture mode is invalid.", "invalid_capture_mode");
            return false;
        }

        if (request.Method is { } parsed)
        {
            if (string.IsNullOrWhiteSpace(parsed.Type) || string.IsNullOrWhiteSpace(parsed.Ref))
            {
                problem = ApiAnswer.Problem(422, "The payment method is invalid.", "invalid_method");
                return false;
            }

            method = new PaymentMethodRef(parsed.Type, parsed.Ref);
        }

        amount = new Money(request.AmountMinor, currency);
        return true;
    }
}
