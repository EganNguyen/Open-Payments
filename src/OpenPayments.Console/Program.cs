using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ConsoleSecurityOptions>(configuration.GetSection("Console:Security"));
builder.Services.Configure<ConsoleSecurityOptions>(options =>
{
    var amr = configuration["Console:Oidc:RequiredAmr"];
    if (!string.IsNullOrEmpty(amr))
    {
        options.RequiredAmr = amr;
    }
});

// The primary, as op_console: staff, audit and reads after an action.
builder.Services.AddKeyedSingleton("primary", (_, _) =>
    NpgsqlDataSource.Create(configuration["Console:Db:Primary"] ?? "Host=127.0.0.1;Database=openpayments"));
builder.Services.AddSingleton<IStaffStore>(services =>
    new StaffStore(services.GetRequiredKeyedService<NpgsqlDataSource>("primary")));

// Reads use op_console_read with a 2 s statement timeout: the replica for search and queues,
// the primary by ID after an action.
static NpgsqlDataSource ReadSource(string? connectionString, int timeoutMs)
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString ?? "Host=127.0.0.1;Database=openpayments")
    {
        Options = "-c statement_timeout=" + timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    return NpgsqlDataSource.Create(builder.ConnectionString);
}

var readTimeoutMs = configuration.GetValue("Console:Db:StatementTimeoutMs", 2000);
builder.Services.AddKeyedSingleton("replica", (_, _) => ReadSource(configuration["Console:Db:Replica"], readTimeoutMs));
builder.Services.AddKeyedSingleton("primary-read", (_, _) => ReadSource(configuration["Console:Db:PrimaryRead"], readTimeoutMs));
builder.Services.AddSingleton<IConsoleReadStore>(services => new ConsoleReadStore(
    services.GetRequiredKeyedService<NpgsqlDataSource>("replica"),
    services.GetRequiredKeyedService<NpgsqlDataSource>("primary-read")));
builder.Services.AddSingleton<ITimelineReader>(services => new TimelineReader(
    services.GetRequiredKeyedService<NpgsqlDataSource>("replica"),
    services.GetRequiredKeyedService<NpgsqlDataSource>("primary-read")));
builder.Services.AddSingleton<IAuditWriter>(services =>
    new AuditWriter(services.GetRequiredKeyedService<NpgsqlDataSource>("primary")));
builder.Services.Configure<OpenPayments.Console.Actions.PaymentApiOptions>(configuration.GetSection("Console:Api"));
builder.Services.AddHttpClient<OpenPayments.Console.Actions.PaymentApiClient>();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("OpenPayments.Console");
// Instances must share the key ring, or a form issued by one can't be read by another.
if (configuration["Console:DataProtection:Path"] is { Length: > 0 } keyPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
builder.Services.AddSingleton<OpenPayments.Console.Actions.FormKey>();
builder.Services.AddSingleton<IAdminStore>(services =>
    new AdminStore(services.GetRequiredKeyedService<NpgsqlDataSource>("primary")));
builder.Services.Configure<SearchOptions>(configuration.GetSection("Console:Search"));
builder.Services.Configure<OpenPayments.Console.Pages.Audit.AuditOptions>(configuration.GetSection("Console:Audit"));
builder.Services.Configure<SearchOptions>(options =>
{
    var merchants = configuration.GetSection("Console:Merchants").Get<Guid[]>();
    if (merchants is not null)
    {
        options.Merchants = merchants;
    }
});

// Cookies are Secure in production. The Testing host and the local compose stack (Development)
// serve plain http, so there they follow the request.
var secureCookies = builder.Environment.IsEnvironment("Testing") || builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "op_console";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = secureCookies;
        // Strict would drop the cookie on the return from the identity provider.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.AccessDeniedPath = "/Account/Denied";
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = configuration["Console:Oidc:Authority"];
        // In compose, the browser and the server reach the provider at different addresses.
        if (configuration["Console:Oidc:MetadataAddress"] is { Length: > 0 } metadata)
        {
            options.MetadataAddress = metadata;
        }

        options.ClientId = configuration["Console:Oidc:ClientId"];
        options.ClientSecret = configuration["Console:Oidc:ClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false; // no tokens kept for the browser
        options.MapInboundClaims = false; // keep sub, amr and auth_time as the provider sent them
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    });

builder.Services.AddSingleton<IAuthorizationHandler, StaffAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    foreach (var policy in ConsolePolicies.All)
    {
        options.AddPolicy(policy, rule => rule.RequireAuthenticatedUser().AddRequirements(new StaffRequirement(policy)));
    }

    // Every page needs an active staff member, unless it opts out.
    options.FallbackPolicy = options.GetPolicy(ConsolePolicies.CanRead);
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToFolder("/Account");
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "op_console_af";
    options.Cookie.SecurePolicy = secureCookies;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Self-hosted assets only: no request to any other origin.
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; " +
        "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

if (app.Environment.IsEnvironment("Testing"))
{
    // One probe per policy, so tests can prove the server enforces each one.
    foreach (var policy in ConsolePolicies.All)
    {
        app.MapGet("/test/policy/" + policy, () => Results.Ok()).RequireAuthorization(policy);
    }
}

await app.RunAsync();

public partial class Program;
