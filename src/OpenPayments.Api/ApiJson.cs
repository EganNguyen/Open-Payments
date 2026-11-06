using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPayments.Domain;
using OpenPayments.Transitions;

namespace OpenPayments.Api;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record CreatePaymentRequest(
    long AmountMinor,
    string Currency,
    MethodRequest? Method,
    string CaptureMode = "manual",
    bool ErrorOnAction = false);

public sealed record MethodRequest(string Type, string Ref, JsonElement? Details = null);

public sealed record CreateAttemptRequest(MethodRequest Method, bool ErrorOnAction = false);

public sealed record CaptureRequest(long AmountMinor);

public sealed record RefundRequest(long AmountMinor);

public sealed record RefundBody(
    string Id,
    string PaymentId,
    long AmountMinor,
    string State,
    DateTimeOffset CreatedAt)
{
    public static RefundBody From(RefundRecord record) =>
        new(
            record.Id.Value.ToString("D"),
            record.PaymentId.Value.ToString("D"),
            record.Refund.Amount.Minor,
            PaymentStateText.Refund(record.Refund.State),
            record.Refund.CreatedAt);

    public static RefundBody From(RefundView refund, PaymentId payment) =>
        new(
            refund.Id.Value.ToString("D"),
            payment.Value.ToString("D"),
            refund.Amount.Minor,
            PaymentStateText.Refund(refund.State),
            refund.CreatedAt);
}

public sealed record ProblemBody(string Title, int Status, string Code)
{
    public string Type { get; init; } = "about:blank";
}

public sealed record AttemptBody(string Id, string State, string? FailureCode)
{
    public static AttemptBody From(AttemptView? attempt) =>
        attempt is null
            ? null!
            : new AttemptBody(
                attempt.Id.Value.ToString("D"),
                PaymentStateText.Attempt(attempt.State),
                attempt.FailureCode);
}

public sealed record PaymentBody(
    string Id,
    string State,
    long AmountMinor,
    string Currency,
    long CapturedMinor,
    long RefundedMinor,
    long RefundPendingMinor,
    AttemptBody? LatestAttempt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt)
{
    public static PaymentBody From(PaymentView view) =>
        new(
            view.Id.Value.ToString("D"),
            PaymentStateText.Payment(view.State),
            view.Amount.Minor,
            view.Amount.Currency.Value,
            view.CapturedMinor,
            view.RefundedMinor,
            view.RefundPendingMinor,
            AttemptBody.From(view.Attempt),
            view.ExpiresAt,
            view.CreatedAt);
}

public static class PaymentStateText
{
    public static string Payment(PaymentState state) => state switch
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
        _ => state.ToString().ToUpperInvariant(),
    };

    public static string Attempt(AttemptState state) => state switch
    {
        AttemptState.Pending => "PENDING",
        AttemptState.ActionRequired => "ACTION_REQUIRED",
        AttemptState.Unknown => "UNKNOWN",
        AttemptState.Succeeded => "SUCCEEDED",
        AttemptState.Failed => "FAILED",
        AttemptState.Review => "REVIEW",
        _ => state.ToString().ToUpperInvariant(),
    };

    public static string Refund(RefundState state) => state switch
    {
        RefundState.Pending => "PENDING",
        RefundState.Unknown => "UNKNOWN",
        RefundState.Succeeded => "SUCCEEDED",
        RefundState.Failed => "FAILED",
        RefundState.Review => "REVIEW",
        _ => state.ToString().ToUpperInvariant(),
    };
}

public sealed record ApiAnswer(int StatusCode, object? Body, int? RetryAfter = null, string? RawJson = null)
{
    public static ApiAnswer Json(int status, object body) => new(status, body);

    public static ApiAnswer Problem(int status, string title, string code, int? retryAfter = null) =>
        new(status, new ProblemBody(title, status, code), retryAfter);

    public static ApiAnswer Payment(PaymentView view, int status = 200) =>
        new(status, PaymentBody.From(view));
}
