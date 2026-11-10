using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

/// <summary>The operation a key belongs to, for rendering the answer a replay returns (D61).</summary>
public abstract record OperationRef
{
    public sealed record Attempt(AttemptId Id, AttemptOperation Operation) : OperationRef;
    public sealed record Refund(RefundId Id) : OperationRef;
}

/// <summary>
/// The one renderer for a key's stored answer (D61). The shapes are the API's, unchanged byte
/// for byte: the API and the store both call this, so a callback, the reconciler or the request
/// path stores the same body.
/// </summary>
public static class AnswerRenderer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static StoredResponse Render(PaymentView payment, OperationRef operation) =>
        operation switch
        {
            OperationRef.Refund => payment.Refund is { } refund
                ? new StoredResponse(StatusFor(refund.State), Serialize(RefundBody.From(refund, payment.Id)))
                : throw new InvalidOperationException("The refund to render is missing."),
            OperationRef.Attempt => new StoredResponse(
                payment.Attempt is { } attempt ? StatusFor(attempt.State) : 200,
                Serialize(PaymentBody.From(payment))),
            _ => throw new InvalidOperationException("Unknown operation reference."),
        };

    private static int StatusFor(AttemptState state) => state == AttemptState.Failed ? 402 : 200;

    private static int StatusFor(RefundState state) => state == RefundState.Failed ? 402 : 200;

    private static string Serialize<T>(T body) => JsonSerializer.Serialize(body, Options);
}

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
