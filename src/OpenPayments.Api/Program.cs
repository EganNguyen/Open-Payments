using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using OpenPayments.Api;
using OpenPayments.Api.Endpoints;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using OpenPayments.Transitions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.MaxConcurrentConnections = 10_000;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
});
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
var database = NormalizeConnectionString(
    builder.Configuration["DATABASE_URL"]
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "postgres://op_app:op_local_only@127.0.0.1:16432/openpayments");
builder.Services.AddSingleton(_ => new TxRunner(database));
builder.Services.AddSingleton(services =>
{
    var store = new PaymentStore(services.GetRequiredService<TxRunner>());
    var adapters = services.GetRequiredService<IPspAdapterFactory>();
    store.AuthorizationWindowFor = psp => adapters.Capabilities(psp).AuthorizationWindow;
    return store;
});
builder.Services.AddSingleton<IPspEventParser, MockPspEventParser>();
builder.Services.AddSingleton<ICallbackSecretProvider, EnvironmentCallbackSecretProvider>();
builder.Services.AddSingleton<ApiKeyAuthenticator>();
builder.Services.AddSingleton(new InFlightLimiter(
    int.TryParse(builder.Configuration["INFLIGHT_CAP"], out var cap) ? cap : 500));
builder.Services.AddSingleton<IFraudCheck, AllowAllFraudCheck>();
builder.Services.AddSingleton<CanonicalLog>();
builder.Services.AddSingleton<PaymentMetrics>();
builder.Services.AddSingleton<RequestTelemetry>();
builder.Services.AddSingleton<PaymentFlow>();
builder.Services.AddSingleton<ResolutionStore>();
builder.Services.AddHttpClient("psp", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["PSP_URL"] ?? "http://127.0.0.1:9080");
    client.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // C16: a DNS change is seen within a minute, without a restart.
    PooledConnectionLifetime = TimeSpan.FromMinutes(1),
});
builder.Services.AddSingleton<AccountGate>();
builder.Services.AddSingleton<IPspAdapterFactory>(services =>
    new ResilientPspAdapterFactory(
        new MockPspAdapterFactory(services.GetRequiredService<IHttpClientFactory>().CreateClient("psp")),
        services.GetRequiredService<AccountGate>()));

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

var requestMetrics = app.Services.GetRequiredService<PaymentMetrics>();
requestMetrics.PspGate = app.Services.GetRequiredService<AccountGate>();
requestMetrics.PspEvents = app.Services.GetRequiredService<PaymentStore>().Events;
var requestTelemetry = app.Services.GetRequiredService<RequestTelemetry>();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/metrics" || context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    var scope = requestTelemetry.Begin();
    requestMetrics.RequestStarted();
    try
    {
        await next();
    }
    finally
    {
        requestMetrics.RequestFinished(
            System.Diagnostics.Stopwatch.GetElapsedTime(started), scope.PspWait);
        requestTelemetry.End();
        var reason = RejectionReason(context.Response.StatusCode);
        if (reason is null)
        {
            requestMetrics.RequestAdmitted();
        }
        else
        {
            requestMetrics.RequestRejected(reason);
        }
    }
});

app.MapPost("/psp/v1/accounts/{pspAccountId:guid}/events", PspEventsEndpoint.HandleAsync);

app.MapGet("/health", () => Results.Json(new { status = "ok" }, ApiJson.Options));
app.MapGet("/metrics", (PaymentMetrics metrics) =>
    Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8"));

app.MapPost("/v1/payments", async (
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<CreatePaymentRequest>(context);
    if (body is null)
    {
        return ToResult(ApiAnswer.Problem(422, "The request body is invalid.", "invalid_request"), context);
    }

    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body.Raw);
    var answer = await flow.CreatePaymentAsync(
        body.Value, identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapPost("/v1/payments/{id:guid}/attempts", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<CreateAttemptRequest>(context);
    if (body is null)
    {
        return ToResult(ApiAnswer.Problem(422, "The request body is invalid.", "invalid_request"), context);
    }

    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body.Raw);
    var answer = await flow.CreateAttemptAsync(
        new PaymentId(id), body.Value, identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapGet("/v1/payments/{id:guid}", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var answer = await flow.GetPaymentAsync(
        new PaymentId(id), identity!.MerchantId, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapPost("/v1/payments/{id:guid}/capture", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<CaptureRequest>(context);
    if (body is null)
    {
        return ToResult(ApiAnswer.Problem(422, "The request body is invalid.", "invalid_request"), context);
    }

    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body.Raw);
    var answer = await flow.CaptureAsync(
        new PaymentId(id), body.Value, identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapPost("/v1/payments/{id:guid}/void", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<JsonElement>(context);
    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body?.Raw ?? []);
    var answer = await flow.VoidAsync(
        new PaymentId(id), identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapPost("/v1/payments/{id:guid}/refunds", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<RefundRequest>(context);
    if (body is null)
    {
        return ToResult(ApiAnswer.Problem(422, "The request body is invalid.", "invalid_request"), context);
    }

    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body.Raw);
    var answer = await flow.RefundAsync(
        new PaymentId(id), body.Value, identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapGet("/v1/refunds/{id:guid}", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var answer = await flow.GetRefundAsync(
        new RefundId(id), identity!.MerchantId, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapPost("/v1/payments/{id:guid}/cancel", async (
    Guid id,
    HttpContext context,
    ApiKeyAuthenticator auth,
    PaymentFlow flow) =>
{
    var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
    var authError = Authorize(identity);
    if (authError is not null)
    {
        return ToResult(authError, context);
    }

    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!ValidKey(key))
    {
        return ToResult(ApiAnswer.Problem(400, "An idempotency key is required.", "missing_idempotency_key"), context);
    }

    var body = await ReadBodyAsync<JsonElement>(context);
    var hash = PaymentFlow.RequestHash("POST", context.Request.Path, body?.Raw ?? []);
    var answer = await flow.CancelAsync(
        new PaymentId(id), identity!.MerchantId, key, hash, context.RequestAborted);
    return ToResult(answer, context);
});

app.MapInternalRoutes();

app.Run();

static string NormalizeConnectionString(string value)
{
    if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return value;
    }

    var uri = new Uri(value);
    var user = uri.UserInfo.Split(':', 2);
    return new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.Trim('/'),
        Username = Uri.UnescapeDataString(user[0]),
        Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : string.Empty,
        CommandTimeout = 0,
    }.ConnectionString;
}

static ApiAnswer? Authorize(ApiKeyIdentity? identity)
{
    if (identity is null)
    {
        return ApiAnswer.Problem(401, "Authentication is required.", "unauthenticated");
    }

    return identity.Mode == "live"
        ? null
        : ApiAnswer.Problem(403, "Test-mode keys are not enabled yet.", "test_mode_unavailable");
}

static bool ValidKey(string key) => key.Length is >= 1 and <= 255;

static string? RejectionReason(int statusCode) => statusCode switch
{
    401 or 403 => "auth",
    404 => "not_found",
    409 or 422 => "invalid_state",
    429 => "rate_limit",
    503 => "busy",
    _ => null,
};

static IResult ToResult(ApiAnswer answer, HttpContext context)
{
    if (answer.RetryAfter is { } retry)
    {
        context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    return answer.RawJson is { } raw
        ? Results.Content(raw, "application/json", statusCode: answer.StatusCode)
        : Results.Json(answer.Body, ApiJson.Options, statusCode: answer.StatusCode);
}

static async Task<BodyValue<T>?> ReadBodyAsync<T>(HttpContext context)
{
    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
    var raw = await reader.ReadToEndAsync(context.RequestAborted);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    try
    {
        var value = JsonSerializer.Deserialize<T>(raw, ApiJson.Options);
        return value is null ? null : new BodyValue<T>(value, System.Text.Encoding.UTF8.GetBytes(raw));
    }
    catch (JsonException)
    {
        return null;
    }
}

internal sealed record BodyValue<T>(T Value, byte[] Raw);

public partial class Program;
