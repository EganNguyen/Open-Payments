using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenPayments.MockPsp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddSingleton<MockConfigStore>();
builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var directory = configuration["EFFECTS_DIR"]
        ?? Environment.GetEnvironmentVariable("EFFECTS_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "effects");
    Directory.CreateDirectory(directory);
    return new EffectLog(Path.Combine(directory, "effects.csv"));
});
builder.Services.AddSingleton<MockAttemptStore>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CallbackSender>();

var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { status = "ok" }, MockJson.Options));

app.MapPost("/authorize", (
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        OperationBody? body) =>
    MockPspHandlers.HandleOperationAsync(context, store, callbacks, "authorize", body));

app.MapPost("/sale", (
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        OperationBody? body) =>
    MockPspHandlers.HandleOperationAsync(context, store, callbacks, "sale", body));

app.MapPost("/capture", (
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        OperationBody? body) =>
    MockPspHandlers.HandleOperationAsync(context, store, callbacks, "capture", body));

app.MapPost("/refund", (
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        OperationBody? body) =>
    MockPspHandlers.HandleOperationAsync(context, store, callbacks, "refund", body));

app.MapGet("/inquire/{key}", (
        HttpContext context,
        MockAttemptStore store,
        string key) =>
    MockPspHandlers.InquireAsync(context, store, key));

app.MapPost("/void", (
        HttpContext context,
        MockAttemptStore store,
        VoidBody? body) =>
    MockPspHandlers.VoidAsync(context, store, body));

app.MapPost("/cancel-by-reference/{key}", (
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        string key) =>
    MockPspHandlers.CancelByReferenceAsync(context, store, callbacks, key));

app.MapPut("/admin/config", (MockConfigStore config, MockConfigUpdate update) =>
{
    config.Update(update);
    return Results.Json(new { updated = true }, MockJson.Options);
});

app.MapPut("/admin/accounts/{account}", (
    string account,
    MockAttemptStore store,
    MockAccountUpdate update) =>
{
    store.Account(account).Apply(update);
    return Results.Json(new { updated = true }, MockJson.Options);
});

app.MapGet("/admin/stats", (MockAttemptStore store) =>
    Results.Json(store.Stats(), MockJson.Options));

app.Run();

public partial class Program;

public sealed record OperationBody(long AmountMinor, string Currency);

public sealed record VoidBody(string? PspReference, string? Key);

public static class MockJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

public static class MockPspHandlers
{
    public static async Task<IResult> HandleOperationAsync(
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        string op,
        OperationBody? body)
    {
        var account = context.Request.Headers["X-Mock-Account"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(account))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_account");
        }

        var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_idempotency_key");
        }

        if (store.IsRevoked(account))
        {
            store.RecordRevoked();
            return Problem(StatusCodes.Status401Unauthorized, "account_revoked");
        }

        if (!store.TryRateLimit(account))
        {
            return Busy(context);
        }

        if (body is null
            || body.AmountMinor <= 0
            || string.IsNullOrWhiteSpace(body.Currency)
            || body.Currency.Length != 3)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request");
        }

        var config = store.Config;
        var latency = config.LatencyMs + (config.JitterMs > 0 ? Random.Shared.Next(config.JitterMs + 1) : 0);
        if (latency > 0)
        {
            await Task.Delay(latency, context.RequestAborted);
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = store.GetOrCreate(key, op, account, body.AmountMinor, body.Currency, now);

        if (string.Equals(attempt.Outcome, "error", StringComparison.Ordinal))
        {
            return Results.Json(
                new Dictionary<string, object?>
                {
                    ["outcome"] = "error",
                    ["failure_code"] = "mock_error",
                },
                MockJson.Options,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var effectTask = store.TryStartEffect(attempt);
        if (effectTask is not null && attempt.LandingAt <= now)
        {
            await effectTask;
        }

        if (config.CallbackRate > 0
            && Outcomes.Hit(key, config.Seed, "callback", config.CallbackRate))
        {
            await MaybeSendCallbackAsync(
                callbacks,
                config,
                attempt,
                "resolved",
                context.RequestAborted);
        }

        if (attempt.Hang)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }

            return Results.Empty;
        }

        return Results.Json(ResponseBody(attempt), MockJson.Options);
    }

    public static IResult InquireAsync(HttpContext context, MockAttemptStore store, string key)
    {
        var account = context.Request.Headers["X-Mock-Account"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(account))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_account");
        }

        if (store.IsRevoked(account))
        {
            store.RecordRevoked();
            return Problem(StatusCodes.Status401Unauthorized, "account_revoked");
        }

        if (!store.TryRateLimit(account))
        {
            return Busy(context);
        }

        var inquiry = store.Inquire(key, DateTimeOffset.UtcNow);
        var payload = new Dictionary<string, object?>
        {
            ["status"] = inquiry.Status,
        };
        if (inquiry.Attempt is { } attempt)
        {
            payload["psp_reference"] = attempt.PspReference;
            payload["amount_minor"] = attempt.AmountMinor;
            payload["currency"] = attempt.Currency;
        }

        if (inquiry.FailureCode is not null)
        {
            payload["failure_code"] = inquiry.FailureCode;
        }

        if (inquiry.Attempt?.Action is { } action)
        {
            payload["action"] = action;
        }

        return Results.Json(payload, MockJson.Options);
    }

    public static async Task<IResult> CancelByReferenceAsync(
        HttpContext context,
        MockAttemptStore store,
        CallbackSender callbacks,
        string key)
    {
        var account = context.Request.Headers["X-Mock-Account"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(account))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_account");
        }

        if (store.IsRevoked(account))
        {
            store.RecordRevoked();
            return Problem(StatusCodes.Status401Unauthorized, "account_revoked");
        }

        if (!store.TryRateLimit(account))
        {
            return Busy(context);
        }

        var result = store.Cancel(key, DateTimeOffset.UtcNow);
        switch (result)
        {
            case CancelResult.NotFound:
                return Problem(StatusCodes.Status404NotFound, "not_found");
            case CancelResult.AlreadyLanded:
                return Problem(StatusCodes.Status409Conflict, "already_landed");
            case CancelResult.NotCancellable:
                return Problem(StatusCodes.Status409Conflict, "not_cancellable");
            default:
                var attempt = store.FindByKey(key);
                if (attempt is not null)
                {
                    await MaybeSendCallbackAsync(
                        callbacks,
                        store.Config,
                        attempt,
                        "cancelled",
                        context.RequestAborted,
                        force: true);
                }

                return Results.Json(
                    new Dictionary<string, object?> { ["status"] = "cancelled" },
                    MockJson.Options);
        }
    }

    public static async Task<IResult> VoidAsync(
        HttpContext context,
        MockAttemptStore store,
        VoidBody? body)
    {
        var account = context.Request.Headers["X-Mock-Account"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(account))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_account");
        }

        if (store.IsRevoked(account))
        {
            store.RecordRevoked();
            return Problem(StatusCodes.Status401Unauthorized, "account_revoked");
        }

        if (!store.TryRateLimit(account))
        {
            return Busy(context);
        }

        var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problem(StatusCodes.Status400BadRequest, "missing_idempotency_key");
        }

        var target = body?.PspReference is { } reference
            ? store.FindByReference(reference)
            : body?.Key is { } targetKey
                ? store.FindByKey(targetKey)
                : null;

        if (target is null)
        {
            return Problem(StatusCodes.Status404NotFound, "not_found");
        }

        if ((string.Equals(target.Op, "capture", StringComparison.Ordinal)
                || string.Equals(target.Op, "sale", StringComparison.Ordinal))
            && target.EffectWritten != 0)
        {
            return Problem(StatusCodes.Status409Conflict, "already_captured");
        }

        if (!Outcomes.IsApproved(target.Outcome))
        {
            return Problem(StatusCodes.Status409Conflict, "not_voidable");
        }

        var attempt = store.GetOrCreate(
            key,
            "void",
            account,
            target.AmountMinor,
            target.Currency,
            DateTimeOffset.UtcNow);
        var effectTask = store.TryStartEffect(attempt);
        if (effectTask is not null && attempt.LandingAt <= DateTimeOffset.UtcNow)
        {
            await effectTask;
        }

        return Results.Json(ResponseBody(attempt), MockJson.Options);
    }

    private static Dictionary<string, object?> ResponseBody(MockAttempt attempt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["outcome"] = attempt.Outcome,
            ["psp_reference"] = attempt.PspReference,
            ["amount_minor"] = attempt.AmountMinor,
            ["currency"] = attempt.Currency,
        };
        if (attempt.FailureCode is not null)
        {
            payload["failure_code"] = attempt.FailureCode;
        }

        if (attempt.Action is not null)
        {
            payload["action"] = attempt.Action;
        }

        return payload;
    }

    private static async Task MaybeSendCallbackAsync(
        CallbackSender callbacks,
        MockConfig config,
        MockAttempt attempt,
        string status,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (string.IsNullOrWhiteSpace(config.CallbackUrl) || (!force && config.CallbackRate <= 0))
        {
            return;
        }

        if (config.CallbackDelayMs > 0)
        {
            await Task.Delay(config.CallbackDelayMs, cancellationToken);
        }

        var payload = new Dictionary<string, object?>
        {
            ["key"] = attempt.Key,
            ["op"] = attempt.Op,
            ["status"] = status,
            ["psp_reference"] = attempt.PspReference,
            ["amount_minor"] = attempt.AmountMinor,
            ["currency"] = attempt.Currency,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        await callbacks.SendAsync(config.CallbackUrl, payload, cancellationToken);
    }

    private static IResult Problem(int statusCode, string code) =>
        Results.Json(
            new Dictionary<string, object?> { ["error"] = code },
            MockJson.Options,
            statusCode: statusCode);

    private static IResult Busy(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        return Results.Json(
            new Dictionary<string, object?>
            {
                ["error"] = "rate_limited",
                ["retry_after"] = "1",
            },
            MockJson.Options,
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}

public sealed class CallbackSender
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CallbackSender(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> SendAsync(
        string url,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("callbacks");
        client.Timeout = TimeSpan.FromSeconds(2);
        var json = JsonSerializer.Serialize(payload, MockJson.Options);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(url, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }

        return false;
    }
}
