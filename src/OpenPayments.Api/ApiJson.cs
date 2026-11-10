using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPayments.Domain;
using OpenPayments.Transitions;

namespace OpenPayments.Api;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = AnswerRenderer.Options;
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

public sealed record ProblemBody(string Title, int Status, string Code)
{
    public string Type { get; init; } = "about:blank";
}

public sealed record ApiAnswer(int StatusCode, object? Body, int? RetryAfter = null, string? RawJson = null)
{
    public static ApiAnswer Json(int status, object body) => new(status, body);

    public static ApiAnswer Problem(int status, string title, string code, int? retryAfter = null) =>
        new(status, new ProblemBody(title, status, code), retryAfter);

    public static ApiAnswer Payment(PaymentView view, int status = 200) =>
        new(status, PaymentBody.From(view));
}
