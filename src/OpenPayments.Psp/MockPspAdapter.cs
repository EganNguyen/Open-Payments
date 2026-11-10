using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPayments.Domain;

namespace OpenPayments.Psp;

/// <summary>
/// HTTP adapter for tools/OpenPayments.MockPsp. One instance acts for one merchant's
/// psp_accounts row (D35); the attempt or refund id is the provider idempotency key (D5).
/// </summary>
public sealed class MockPspAdapter : IPspAdapter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _client;
    private readonly PspAccount _account;

    public MockPspAdapter(HttpClient client, PspAccount account)
    {
        _client = client;
        _account = account;
    }

    public PspCapabilities Capabilities { get; } = MockCapabilities.Create(supportsSale: true);

    public Task<PspResult> AuthorizeAsync(
        AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct) =>
        SendOperationAsync("authorize", attempt.Value, amount, method, ct);

    public Task<PspResult> SaleAsync(
        AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct) =>
        SendOperationAsync("sale", attempt.Value, amount, method, ct);

    public async Task<PspResult> CompleteActionAsync(AttemptId attempt, CancellationToken ct)
    {
        var inquiry = await RecoverAsync(attempt, ct);
        return inquiry switch
        {
            PspInquiry.Answered answered => answered.Result,
            PspInquiry.ProvenNotCharged notCharged => new PspResult.Declined(notCharged.FailureCode),
            _ => throw new PspUnavailableException("The action result is not available."),
        };
    }

    public Task<PspResult> CaptureAsync(AttemptId attempt, Money amount, CancellationToken ct) =>
        SendOperationAsync("capture", attempt.Value, amount, Method: null, ct);

    public async Task<PspResult> VoidAsync(
        AttemptId attempt, AttemptId authorized, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/void")
        {
            Content = JsonContent.Create(new { key = authorized.Value.ToString("D") }, options: Json),
        };
        AddHeaders(request, attempt.Value);
        return await SendAsync(request, ct);
    }

    public Task<PspResult> RefundAsync(RefundId refund, Money amount, CancellationToken ct) =>
        SendOperationAsync("refund", refund.Value, amount, Method: null, ct);

    public async Task<PspInquiry> RecoverAsync(AttemptId attempt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/inquire/" + attempt.Value.ToString("D"));
        AddHeaders(request, attempt.Value);
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PspUnavailableException($"Mock PSP inquiry answered {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<InquiryPayload>(Json, ct)
            ?? throw new PspUnavailableException("Mock PSP returned no inquiry body.");
        return payload.Status switch
        {
            "approved" => new PspInquiry.Answered(
                new PspResult.Approved(payload.PspReference ?? string.Empty, Amount(payload))),
            "declined" => new PspInquiry.Answered(
                new PspResult.Declined(payload.FailureCode ?? "declined")),
            "action_required" => new PspInquiry.Answered(
                new PspResult.RequiresAction(
                    payload.Action?.Type ?? "three_d_secure",
                    payload.Action?.Url,
                    payload.Action?.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15))),
            "not_found" => new PspInquiry.ProvenNotCharged("not_found"),
            "cancelled" => new PspInquiry.ProvenNotCharged("cancelled"),
            _ => new PspInquiry.Inconclusive(payload.Status ?? "unknown"),
        };
    }

    public async Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/lookup/" + Uri.EscapeDataString(pspReference));
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PspUnavailableException($"Mock PSP lookup answered {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<InquiryPayload>(Json, ct);
        return FromInquiry(payload);
    }

    public async Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/cancel-for-proof/" + attempt.Value.ToString("D"))
        {
            Content = JsonContent.Create(new { psp_reference = pspReference }, options: Json),
        };
        AddHeaders(request, attempt.Value);
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PspUnavailableException($"Mock PSP cancel-for-proof answered {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<InquiryPayload>(Json, ct);
        return FromInquiry(payload);
    }

    private static PspInquiry FromInquiry(InquiryPayload? payload) =>
        payload?.Status switch
        {
            "approved" => new PspInquiry.Answered(
                new PspResult.Approved(payload.PspReference ?? string.Empty, Amount(payload))),
            "declined" => new PspInquiry.Answered(
                new PspResult.Declined(payload.FailureCode ?? "declined")),
            "action_required" => new PspInquiry.Answered(
                new PspResult.RequiresAction(
                    payload.Action?.Type ?? "three_d_secure",
                    payload.Action?.Url,
                    payload.Action?.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15))),
            "not_found" => new PspInquiry.ProvenNotCharged("not_found"),
            "cancelled" => new PspInquiry.ProvenNotCharged("cancelled"),
            _ => new PspInquiry.Inconclusive(payload?.Status ?? "unknown"),
        };

    public async Task<PspInquiry> RecoverRefundAsync(RefundId refund, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/inquire/" + refund.Value.ToString("D"));
        AddHeaders(request, refund.Value);
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new PspInquiry.Inconclusive("inquiry_unavailable");
        }

        var payload = await response.Content.ReadFromJsonAsync<InquiryPayload>(Json, ct);
        return payload?.Status switch
        {
            "approved" => new PspInquiry.Answered(
                new PspResult.Approved(payload.PspReference ?? string.Empty, Amount(payload))),
            "declined" => new PspInquiry.Answered(
                new PspResult.Declined(payload.FailureCode ?? "declined")),
            "not_found" => new PspInquiry.ProvenNotCharged("not_found"),
            "cancelled" => new PspInquiry.ProvenNotCharged("cancelled"),
            _ => new PspInquiry.Inconclusive(payload?.Status ?? "unknown"),
        };
    }

    private async Task<PspResult> SendOperationAsync(
        string operation,
        Guid key,
        Money amount,
        PaymentMethodRef? Method,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/" + operation)
        {
            Content = JsonContent.Create(
                new
                {
                    amount_minor = amount.Minor,
                    currency = amount.Currency.Value,
                    method_type = Method?.Type,
                    method_ref = Method?.Ref,
                },
                options: Json),
        };
        AddHeaders(request, key);
        return await SendAsync(request, ct);
    }

    private async Task<PspResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(request, ct);
        }
        catch (HttpRequestException exception)
        {
            // C16: a name-resolution failure happened before anything was sent.
            if (exception.HttpRequestError == HttpRequestError.NameResolutionError)
            {
                throw new PspDnsException("The PSP's name did not resolve.", exception);
            }

            // D27: the request may have reached the PSP, so the outcome is unknown, never failed.
            throw new PspUnavailableException("The PSP connection failed.", exception);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new PspUnavailableException("The PSP answer couldn't be read.", exception);
        }
    }

    private async Task<PspResult> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                ?? TimeSpan.FromSeconds(1);
            throw new PspRateLimitException(retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PspUnauthorizedException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PspUnavailableException($"Mock PSP answered {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OperationPayload>(Json, ct)
            ?? throw new PspUnavailableException("Mock PSP returned no body.");
        return payload.Outcome switch
        {
            "approved" => new PspResult.Approved(payload.PspReference ?? string.Empty, Amount(payload)),
            "declined" => new PspResult.Declined(payload.FailureCode ?? "declined"),
            "action_required" => new PspResult.RequiresAction(
                payload.Action?.Type ?? "three_d_secure",
                payload.Action?.Url,
                payload.Action?.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15)),
            _ => throw new PspUnavailableException($"Mock PSP returned outcome '{payload.Outcome}'."),
        };
    }

    private void AddHeaders(HttpRequestMessage request, Guid key)
    {
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-Mock-Account", _account.Id.ToString("D"));
    }

    /// <summary>PSPs differ in case (Stripe answers "usd"). A code the platform doesn't know is
    /// an answer the adapter can't read, so the outcome is unknown, never a 500 (review M8).</summary>
    private static CurrencyCode ReadCurrency(string currency) =>
        CurrencyCode.TryParse(currency.ToUpperInvariant(), out var code)
            ? code
            : throw new PspUnavailableException($"The PSP answered in an unknown currency '{currency}'.");

    private static Money? Amount(OperationPayload payload) =>
        payload.AmountMinor is { } minor && payload.Currency is { Length: 3 } currency
            ? new Money(minor, ReadCurrency(currency))
            : null;

    private static Money? Amount(InquiryPayload payload) =>
        payload.AmountMinor is { } minor && payload.Currency is { Length: 3 } currency
            ? new Money(minor, ReadCurrency(currency))
            : null;

    private sealed record OperationPayload(
        string Outcome,
        string? PspReference,
        long? AmountMinor,
        string? Currency,
        string? FailureCode,
        ActionPayload? Action);

    private sealed record InquiryPayload(
        string? Status,
        string? PspReference,
        long? AmountMinor,
        string? Currency,
        string? FailureCode,
        ActionPayload? Action);

    private sealed record ActionPayload(string Type, string? Url, DateTimeOffset ExpiresAt);
}

public static class MockCapabilities
{
    public static PspCapabilities Create(bool supportsSale) => new(
        SupportsSale: supportsSale,
        Unanswered: RecoveryMode.AuthoritativeLookup,
        ConsistentLookupByReference: true,
        ActionProof: ActionProof.ConfirmedCancel,
        KeyRetention: TimeSpan.FromHours(2),
        ReplayWindow: TimeSpan.FromMinutes(30),
        AuthorizationWindow: TimeSpan.FromDays(7),
        SettlementLag: TimeSpan.FromHours(1),
        LookupQuietWindow: TimeSpan.Zero);
}

public sealed class MockPspAdapterFactory(HttpClient client) : IPspAdapterFactory
{
    public PspCapabilities Capabilities(PspAccount account) => MockCapabilities.Create(supportsSale: true);

    public IPspAdapter For(PspAccount account) => new MockPspAdapter(client, account);
}
