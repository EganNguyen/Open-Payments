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

    public PspCapabilities Capabilities { get; } =
        new(SupportsSale: true, RecoveryMode: "authoritative_lookup", KeyRetention: TimeSpan.FromHours(2));

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
        using var response = await _client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new PspUnavailableException("The PSP rate limited the account.");
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

    private static Money? Amount(OperationPayload payload) =>
        payload.AmountMinor is { } minor && payload.Currency is { Length: 3 } currency
            ? new Money(minor, CurrencyCode.Parse(currency))
            : null;

    private static Money? Amount(InquiryPayload payload) =>
        payload.AmountMinor is { } minor && payload.Currency is { Length: 3 } currency
            ? new Money(minor, CurrencyCode.Parse(currency))
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

public sealed class MockPspAdapterFactory(HttpClient client) : IPspAdapterFactory
{
    public PspCapabilities Capabilities(PspAccount account) =>
        new(SupportsSale: true, RecoveryMode: "authoritative_lookup", KeyRetention: TimeSpan.FromHours(2));

    public IPspAdapter For(PspAccount account) => new MockPspAdapter(client, account);
}
