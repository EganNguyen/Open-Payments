using OpenPayments.Domain;

namespace OpenPayments.Transitions;

internal static class Sql
{
    public const string LoadPayment = """
        select id, merchant_id, state, amount_minor, currency, captured_minor,
               refunded_minor, refund_pending_minor, attempt_count, event_seq,
               version, expires_at, created_at
        from payments
        where id = @payment_id and merchant_id = @merchant_id
        """;

    public const string LoadAttempt = """
        select attempt_id, operation, state, target_attempt_id, failure_code, psp, psp_account_id,
               idempotency_key, resolves_key
        from payment_attempts
        where payment_id = @payment_id
        order by created_at desc
        limit 1
        """;

    public const string LoadRefund = """
        select id, state, amount_minor, psp, psp_account_id, created_at,
               idempotency_key, resolves_key
        from refunds
        where payment_id = @payment_id
        order by created_at desc
        limit 1
        """;

    public const string ReadKey = """
        select request_hash, response_code, response_body, payment_id, attempt_id, refund_id, resolved_at,
               now() - created_at as age,
               (select r.state from refunds r where r.id = idempotency_keys.refund_id) as refund_state
        from idempotency_keys
        where merchant_id = @merchant and key = @key
        """;

    public const string AccountId = """
        select id
        from accounts
        where merchant_id = @merchant
          and account_type = @account_type
          and currency = @currency
          and sub_account = 0
        """;

    public const string PaymentState = "select state from payments where id = @payment_id";
}

internal static class StateText
{
    public static PaymentState Payment(string value) =>
        value switch
        {
            "AWAITING_METHOD" => PaymentState.AwaitingMethod,
            "PSP_PENDING" => PaymentState.PspPending,
            "ACTION_REQUIRED" => PaymentState.ActionRequired,
            "UNKNOWN" => PaymentState.Unknown,
            "REVIEW" => PaymentState.Review,
            "AUTHORIZED" => PaymentState.Authorized,
            "CAPTURED" => PaymentState.Captured,
            "PARTIALLY_REFUNDED" => PaymentState.PartiallyRefunded,
            "REFUNDED" => PaymentState.Refunded,
            "CANCELED" => PaymentState.Canceled,
            "VOIDED" => PaymentState.Voided,
            "EXPIRED" => PaymentState.Expired,
            _ => throw new InvalidOperationException($"Unknown payment state '{value}'."),
        };

    public static string Payment(PaymentState value) =>
        value switch
        {
            PaymentState.AwaitingMethod => "AWAITING_METHOD",
            PaymentState.PspPending => "PSP_PENDING",
            PaymentState.ActionRequired => "ACTION_REQUIRED",
            PaymentState.Unknown => "UNKNOWN",
            PaymentState.Review => "REVIEW",
            PaymentState.Authorized => "AUTHORIZED",
            PaymentState.Captured => "CAPTURED",
            PaymentState.PartiallyRefunded => "PARTIALLY_REFUNDED",
            PaymentState.Refunded => "REFUNDED",
            PaymentState.Canceled => "CANCELED",
            PaymentState.Voided => "VOIDED",
            PaymentState.Expired => "EXPIRED",
            _ => throw new InvalidOperationException($"Unknown payment state '{value}'."),
        };

    public static AttemptState Attempt(string value) =>
        value switch
        {
            "PENDING" => AttemptState.Pending,
            "ACTION_REQUIRED" => AttemptState.ActionRequired,
            "UNKNOWN" => AttemptState.Unknown,
            "SUCCEEDED" => AttemptState.Succeeded,
            "FAILED" => AttemptState.Failed,
            "REVIEW" => AttemptState.Review,
            _ => throw new InvalidOperationException($"Unknown attempt state '{value}'."),
        };

    public static string Attempt(AttemptState value) =>
        value switch
        {
            AttemptState.Pending => "PENDING",
            AttemptState.ActionRequired => "ACTION_REQUIRED",
            AttemptState.Unknown => "UNKNOWN",
            AttemptState.Succeeded => "SUCCEEDED",
            AttemptState.Failed => "FAILED",
            AttemptState.Review => "REVIEW",
            _ => throw new InvalidOperationException($"Unknown attempt state '{value}'."),
        };

    public static AttemptOperation Operation(string value) =>
        value switch
        {
            "authorize" => AttemptOperation.Authorize,
            "sale" => AttemptOperation.Sale,
            "capture" => AttemptOperation.Capture,
            "void" => AttemptOperation.Void,
            _ => throw new InvalidOperationException($"Unknown operation '{value}'."),
        };

    public static string Operation(AttemptOperation value) =>
        value switch
        {
            AttemptOperation.Authorize => "authorize",
            AttemptOperation.Sale => "sale",
            AttemptOperation.Capture => "capture",
            AttemptOperation.Void => "void",
            _ => throw new InvalidOperationException($"Unknown operation '{value}'."),
        };

    public static RefundState Refund(string value) =>
        value switch
        {
            "PENDING" => RefundState.Pending,
            "UNKNOWN" => RefundState.Unknown,
            "SUCCEEDED" => RefundState.Succeeded,
            "FAILED" => RefundState.Failed,
            "REVIEW" => RefundState.Review,
            _ => throw new InvalidOperationException($"Unknown refund state '{value}'."),
        };

    public static string Refund(RefundState value) =>
        value switch
        {
            RefundState.Pending => "PENDING",
            RefundState.Unknown => "UNKNOWN",
            RefundState.Succeeded => "SUCCEEDED",
            RefundState.Failed => "FAILED",
            RefundState.Review => "REVIEW",
            _ => throw new InvalidOperationException($"Unknown refund state '{value}'."),
        };
}
