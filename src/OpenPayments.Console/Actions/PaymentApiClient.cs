using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenPayments.Console.Actions;

public abstract record ApiAnswer
{
    /// <summary>The API applied it, or replayed the answer it stored for this key.</summary>
    public sealed record Done(string State) : ApiAnswer;

    /// <summary>202 or a timeout: the outcome isn't known yet. Never shown as failed.</summary>
    public sealed record StillChecking : ApiAnswer;

    /// <summary>409: the payment's state doesn't allow it now. Reload and explain; don't retry.</summary>
    public sealed record Conflict(string Reason) : ApiAnswer;

    /// <summary>The PSP declined, or the request was refused (402, 400, 404, 422).</summary>
    public sealed record Refused(string Reason) : ApiAnswer;

    /// <summary>429 or 503: retry with the same key.</summary>
    public sealed record Busy(TimeSpan? RetryAfter) : ApiAnswer;
}

public sealed class PaymentApiOptions
{
    public Uri? BaseUrl { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Each merchant's service credential, by merchant ID. From configuration only,
    /// never from a page.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The console's own key for the internal resolution route, from configuration.</summary>
    public string? ResolutionKey { get; set; }
}

/// <summary>Refund, capture, void and cancel through the payment API, so the console has no
/// second code path that moves money.</summary>
public sealed class PaymentApiClient(HttpClient http, IOptions<PaymentApiOptions> options)
{
    public Task<ApiAnswer> CaptureAsync(Guid merchantId, Guid paymentId, long amountMinor, string key, CancellationToken ct) =>
        SendAsync(merchantId, $"v1/payments/{paymentId:D}/capture", new { amount_minor = amountMinor }, key, ct);

    public Task<ApiAnswer> VoidAsync(Guid merchantId, Guid paymentId, string key, CancellationToken ct) =>
        SendAsync(merchantId, $"v1/payments/{paymentId:D}/void", new { }, key, ct);

    public Task<ApiAnswer> CancelAsync(Guid merchantId, Guid paymentId, string key, CancellationToken ct) =>
        SendAsync(merchantId, $"v1/payments/{paymentId:D}/cancel", new { }, key, ct);

    public Task<ApiAnswer> RefundAsync(Guid merchantId, Guid paymentId, long amountMinor, string key, CancellationToken ct) =>
        SendAsync(merchantId, $"v1/payments/{paymentId:D}/refunds", new { amount_minor = amountMinor }, key, ct);

    /// <summary>C5: apply an approved proposal through the internal route.</summary>
    public async Task<ApiAnswer> ApplyResolutionAsync(Guid resolutionId, Guid approverId, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.ResolutionKey) || settings.BaseUrl is null)
        {
            return new ApiAnswer.Refused("no_credential");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(settings.BaseUrl, $"internal/v1/resolutions/{resolutionId:D}/apply"))
        {
            Content = JsonContent.Create(new { approver_staff_id = approverId }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", settings.ResolutionKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            return new ApiAnswer.StillChecking();
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ApiAnswer.Done(StateOf(body)),
                HttpStatusCode.Conflict => new ApiAnswer.Conflict(ErrorOf(body)),
                HttpStatusCode.Forbidden => new ApiAnswer.Refused(ErrorOf(body)),
                HttpStatusCode.Unauthorized => new ApiAnswer.Refused("unauthenticated"),
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                    new ApiAnswer.Busy(response.Headers.RetryAfter?.Delta),
                _ => new ApiAnswer.Refused(ErrorOf(body)),
            };
        }
    }

    private static string StateOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("state", out var state)
            ? state.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? "refused"
                : "refused";
        }
        catch (JsonException)
        {
            return "refused";
        }
    }

    private async Task<ApiAnswer> SendAsync(Guid merchantId, string path, object body, string key, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Keys.TryGetValue(merchantId.ToString("D"), out var credential))
        {
            return new ApiAnswer.Refused("no_credential");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl!, path))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("Idempotency-Key", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            // The request may have reached the API: the outcome is unknown, not failed.
            return new ApiAnswer.StillChecking();
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            var json = await ReadJsonAsync(response, CancellationToken.None);
            return code switch
            {
                200 or 201 => new ApiAnswer.Done(Field(json, "state") ?? "unknown"),
                202 => new ApiAnswer.StillChecking(),
                409 => new ApiAnswer.Conflict(Field(json, "code") ?? "conflict"),
                429 or 503 => new ApiAnswer.Busy(response.Headers.RetryAfter?.Delta),
                402 => new ApiAnswer.Refused(Field(json, "code") ?? "declined"),
                >= 500 => new ApiAnswer.StillChecking(),
                _ => new ApiAnswer.Refused(Field(json, "code") ?? ((HttpStatusCode)code).ToString()),
            };
        }
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Field(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
}
