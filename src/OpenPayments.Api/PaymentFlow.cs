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
    PaymentMetrics metrics)
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

            if (early is KeyLookup.Unresolved or KeyLookup.InFlight)
            {
                return ApiAnswer.Problem(409, "A request is in flight.", "request_in_flight", retryAfter: 1);
            }

            var paymentId = new PaymentId(Guid.NewGuid());
            var account = await store.GetPspAccountAsync(merchant, cancellationToken);
            var supportsSale = account is not null
                && adapters.Capabilities(new PspAccount(
                    account.Id, merchant, account.Psp, account.Mode, account.CredentialRef)).SupportsSale;
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
                return await FromBeginAsync(created, merchant, key, null, requestHash, cancellationToken);
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
                var authorized = await ExecuteAttemptAsync(
                    started, merchant, key, method, cancellationToken);
                if (authorized.StatusCode != 200)
                {
                    return authorized;
                }

                return await CaptureAsync(
                    paymentId,
                    new CaptureRequest(amount.Minor),
                    merchant,
                    key + ":capture",
                    RequestHash("POST", "/internal/capture", ReadOnlySpan<byte>.Empty),
                    cancellationToken);
            }

            return await FromBeginAsync(begin, merchant, key, method, requestHash, cancellationToken);
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

            if (early is KeyLookup.Unresolved or KeyLookup.InFlight)
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
            return await FromBeginAsync(begin, merchant, key, method, requestHash, cancellationToken);
        }
        catch (CommitOutcomeUnknownException exception)
        {
            return ApiAnswer.Problem(503, "The outcome is not known yet.", exception.Reason, retryAfter: 1);
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
        var existing = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (existing is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        var result = await store.BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Cancel(DateTimeOffset.UtcNow),
            attemptId: null,
            refundId: null,
            cancellationToken);
        if (result is BeginOperationResult.Started started)
        {
            var body = JsonSerializer.Serialize(PaymentBody.From(started.Payment), ApiJson.Options);
            await store.ResolveKeyAsync(merchant, key, new StoredResponse(200, body), cancellationToken);
            return new ApiAnswer(200, null, RawJson: body);
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
        CancellationToken cancellationToken)
    {
        var view = await store.GetPaymentAsync(merchant, paymentId, cancellationToken);
        if (view is null)
        {
            return ApiAnswer.Problem(404, "Payment not found.", "not_found");
        }

        if (request.AmountMinor != view.Amount.Minor)
        {
            return ApiAnswer.Problem(422, "The amount must equal the authorized amount.", "capture_amount_mismatch");
        }

        var attemptId = new AttemptId(Guid.NewGuid());
        var begin = await store.BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Capture(attemptId, view.Amount, DateTimeOffset.UtcNow),
            attemptId,
            refundId: null,
            cancellationToken);
        if (begin is not BeginOperationResult.Started started)
        {
            return FromOperation(begin);
        }

        var attempt = started.Payment.Attempt ?? throw new InvalidOperationException("Capture has no attempt.");
        var pspAccount = new PspAccount(attempt.PspAccountId, merchant, attempt.Psp, "live", string.Empty);
        var adapter = adapters.For(pspAccount);
        PspResult result;
        try
        {
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.CaptureAsync(attempt.Id, view.Amount, deadline.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or PspTimeoutException or PspUnavailableException)
        {
            var timedOut = await store.ApplyAsync(
                paymentId,
                new PaymentCommand.CaptureAnswer(
                    attempt.Id, false, null, "timeout", TimedOut: true, DateTimeOffset.UtcNow),
                null,
                CancellationToken.None);
            return await FromAppliedAsync(timedOut, merchant, key, 202, 0, timedOut: true);
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
        var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
        return await FromAppliedAsync(applied, merchant, key, status, 0, timedOut: false);
    }

    public async Task<ApiAnswer> VoidAsync(
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
            return ApiAnswer.Problem(422, "The payment is not authorized.", "invalid_state");
        }

        var attemptId = new AttemptId(Guid.NewGuid());
        var begin = await store.BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.Void(attemptId, authorized.Value, DateTimeOffset.UtcNow),
            attemptId,
            refundId: null,
            cancellationToken);
        if (begin is not BeginOperationResult.Started started)
        {
            return FromOperation(begin);
        }

        var attempt = started.Payment.Attempt ?? throw new InvalidOperationException("Void has no attempt.");
        var pspAccount = new PspAccount(attempt.PspAccountId, merchant, attempt.Psp, "live", string.Empty);
        var adapter = adapters.For(pspAccount);
        PspResult result;
        try
        {
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.VoidAsync(attempt.Id, authorized.Value, deadline.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or PspTimeoutException or PspUnavailableException)
        {
            var timedOut = await store.ApplyAsync(
                paymentId,
                new PaymentCommand.VoidAnswer(attempt.Id, false, TimedOut: true, "timeout", DateTimeOffset.UtcNow),
                null,
                CancellationToken.None);
            return await FromAppliedAsync(timedOut, merchant, key, 202, 0, timedOut: true);
        }

        var answer = result is PspResult.Approved
            ? new PaymentCommand.VoidAnswer(attempt.Id, true, false, null, DateTimeOffset.UtcNow)
            : new PaymentCommand.VoidAnswer(
                attempt.Id, false, false,
                (result as PspResult.Declined)?.FailureCode ?? "declined",
                DateTimeOffset.UtcNow);
        var status = result is PspResult.Approved ? 200 : 402;
        var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
        return await FromAppliedAsync(applied, merchant, key, status, 0, timedOut: false);
    }

    public async Task<ApiAnswer> RefundAsync(
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
            return ApiAnswer.Problem(422, "The refund amount is invalid.", "refund_exceeds_captured");
        }

        var refundId = RefundId.New();
        var amount = new Money(request.AmountMinor, view.Amount.Currency);
        var begin = await store.BeginOperationAsync(
            merchant,
            paymentId,
            key,
            requestHash,
            new PaymentCommand.RefundReserve(refundId, amount, DateTimeOffset.UtcNow),
            attemptId: null,
            refundId,
            cancellationToken);
        if (begin is not BeginOperationResult.Started started)
        {
            return FromOperation(begin);
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
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = await adapter.RefundAsync(refundId, amount, deadline.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or PspTimeoutException or PspUnavailableException)
        {
            var timedOut = await store.ApplyAsync(
                paymentId,
                new PaymentCommand.RefundTimeout(refundId, DateTimeOffset.UtcNow),
                null,
                CancellationToken.None);
            return await FromRefundAppliedAsync(timedOut, merchant, key, 202, refundId, cancellationToken);
        }

        PaymentCommand answer = result switch
        {
            PspResult.Approved => new PaymentCommand.RefundConfirm(refundId, amount, DateTimeOffset.UtcNow),
            PspResult.Declined declined => new PaymentCommand.RefundRelease(
                refundId, ProvenNotCharged: true, declined.FailureCode, DateTimeOffset.UtcNow),
            _ => new PaymentCommand.RefundTimeout(refundId, DateTimeOffset.UtcNow),
        };
        var status = result is PspResult.Approved ? 200 : 402;
        var applied = await store.ApplyAsync(paymentId, answer, null, CancellationToken.None);
        return await FromRefundAppliedAsync(applied, merchant, key, status, refundId, cancellationToken);
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
        RefundId refundId,
        CancellationToken cancellationToken)
    {
        if (result is not ApplyResult.Applied applied)
        {
            return FromOperation(new BeginOperationResult.Rejected("invalid_state"));
        }

        if (applied.Payment.Refund is null)
        {
            return FromOperation(new BeginOperationResult.Rejected("invalid_state", applied.Payment));
        }

        var refund = applied.Payment.Refund;
        var body = JsonSerializer.Serialize(
            RefundBody.From(refund, applied.Payment.Id), ApiJson.Options);
        await store.ResolveKeyAsync(
            merchant, key, new StoredResponse(status, body), cancellationToken);
        return new ApiAnswer(status, null, RawJson: body);
    }

    private static ApiAnswer FromOperation(BeginOperationResult result) => result switch
    {
        BeginOperationResult.Replay replay => new ApiAnswer(
            replay.Response.StatusCode, null, RawJson: replay.Response.Body),
        BeginOperationResult.KeyInFlight => ApiAnswer.Problem(
            409, "A request is in flight.", "request_in_flight", retryAfter: 1),
        BeginOperationResult.HashMismatch => ApiAnswer.Problem(
            422, "The key was used for another request.", "idempotency_key_reused"),
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
        CancellationToken cancellationToken)
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
                    started, merchant, key, method ?? new PaymentMethodRef("card", "tok_test"), cancellationToken);
            default:
                return ApiAnswer.Problem(500, "Unhandled answer.", "internal");
        }
    }

    private async Task<ApiAnswer> ExecuteAttemptAsync(
        BeginResult.Started started,
        MerchantId merchant,
        string key,
        PaymentMethodRef method,
        CancellationToken cancellationToken)
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
            using var deadline = new CancellationTokenSource(PspDeadline);
            result = operation == AttemptOperation.Sale
                ? await adapter.SaleAsync(attempt.Id, view.Amount, method, deadline.Token)
                : await adapter.AuthorizeAsync(attempt.Id, view.Amount, method, deadline.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or PspTimeoutException or PspUnavailableException)
        {
            pspWatch.Stop();
            var timedOut = await store.ApplyAsync(
                view.Id,
                new PaymentCommand.Timeout(attempt.Id, operation, DateTimeOffset.UtcNow),
                null,
                CancellationToken.None);
            return await FromAppliedAsync(timedOut, merchant, key, 202, pspWatch.ElapsedMilliseconds, timedOut: true);
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
            _ => throw new InvalidOperationException("Unknown PSP result."),
        };

        try
        {
            var applied = await store.ApplyAsync(view.Id, answer, null, CancellationToken.None);
            var status = result is PspResult.Declined ? 402 : 200;
            if (result is PspResult.RequiresAction)
            {
                status = 200;
            }

            return await FromAppliedAsync(applied, merchant, key, status, pspWatch.ElapsedMilliseconds, false);
        }
        catch (Exception)
        {
            var current = await store.GetPaymentAsync(merchant, view.Id, CancellationToken.None);
            var body = current is null ? "{}" : JsonSerializer.Serialize(PaymentBody.From(current), ApiJson.Options);
            return new ApiAnswer(202, null, RawJson: body);
        }
    }

    private async Task<ApiAnswer> FromAppliedAsync(
        ApplyResult result,
        MerchantId merchant,
        string key,
        int status,
        long pspWaitMs,
        bool timedOut)
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
                await store.ResolveKeyAsync(
                    merchant, key, new StoredResponse(finalStatus, body), CancellationToken.None);
                if (applied.Payment.State == PaymentState.Captured)
                {
                    metrics.CompletedPayment();
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
