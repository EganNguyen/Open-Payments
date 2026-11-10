using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Tests;

/// <summary>
/// The console with a test sign-in. A request names its person with headers, standing in for
/// the cookie an OIDC sign-in would leave: X-Test-Sub, X-Test-Amr and X-Test-Auth-Age (seconds).
/// Staff rows live in an in-memory store unless a later task swaps in the database.
/// </summary>
public class ConsoleFactory : WebApplicationFactory<Program>
{
    public InMemoryStaffStore Staff { get; } = new();

    public FakePaymentApi Api { get; } = new();

    public FakeOperationsMetrics Metrics { get; } = new();

    /// <summary>Read staff from console.staff, as the service does, instead of memory.</summary>
    public bool UseDatabaseStaff { get; init; }

    public OpenPayments.Console.Data.IAuditWriter? AuditWriterOverride { get; set; }

    /// <summary>Wraps the real read store, for lag and failure cases.</summary>
    public Func<OpenPayments.Console.Data.IConsoleReadStore, OpenPayments.Console.Data.IConsoleReadStore>? DecorateReadStore { get; set; }

    /// <summary>Extra configuration, applied after the defaults. Set before the first client.</summary>
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

    // Starts at the real time: tests seed rows with the database's now(), and a fixed date put
    // them outside the pages' date ranges once the calendar passed it. Tests still move it.
    public FakeTime Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Console:Db:Primary", "Host=127.0.0.1;Port=1;Database=unused");
        builder.UseSetting("Console:Db:Replica", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
        builder.UseSetting("Console:Db:PrimaryRead", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
        builder.UseSetting("Console:Api:BaseUrl", "http://api.test/");
        builder.UseSetting("Console:Oidc:Authority", "https://idp.test/");
        builder.UseSetting("Console:Oidc:ClientId", "console-tests");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            if (!UseDatabaseStaff)
            {
                services.RemoveAll<IStaffStore>();
                services.AddSingleton<IStaffStore>(Staff);
            }
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuth.SchemeName;
                    options.DefaultChallengeScheme = TestAuth.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuth.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.SchemeName, _ => { });
            if (DecorateReadStore is { } decorate)
            {
                var inner = services.Single(d => d.ServiceType == typeof(OpenPayments.Console.Data.IConsoleReadStore));
                services.Remove(inner);
                services.AddSingleton<OpenPayments.Console.Data.IConsoleReadStore>(provider =>
                    decorate((OpenPayments.Console.Data.IConsoleReadStore)inner.ImplementationFactory!(provider)));
            }

            services.AddHttpClient<OpenPayments.Console.Actions.PaymentApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new FakeHandlerProxy(Api));
            if (AuditWriterOverride is { } audit)
            {
                services.RemoveAll<OpenPayments.Console.Data.IAuditWriter>();
                services.AddSingleton(audit);
            }

            services.RemoveAll<OpenPayments.Console.Data.IOperationsMetrics>();
            services.AddSingleton<OpenPayments.Console.Data.IOperationsMetrics>(Metrics);

            ConfigureMoreServices(services);
        });
    }

    protected virtual void ConfigureMoreServices(IServiceCollection services)
    {
    }

    public HttpClient ClientFor(string? subject, string amr = "mfa", int authAgeSeconds = 30)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        if (subject is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Sub", subject);
            client.DefaultRequestHeaders.Add("X-Test-Amr", amr);
            client.DefaultRequestHeaders.Add("X-Test-Auth-Age", authAgeSeconds.ToString(CultureInfo.InvariantCulture));
        }

        return client;
    }

    public Staff AddStaff(ConsoleRole role, string? subject = null)
    {
        var staff = new Staff(Guid.NewGuid(), subject ?? "sub-" + Guid.NewGuid().ToString("N"), "person@example.test", role, null);
        Staff.Put(staff);
        return staff;
    }
}

public sealed class TestAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers["X-Test-Sub"].ToString();
        if (string.IsNullOrEmpty(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var age = int.Parse(Request.Headers["X-Test-Auth-Age"].ToString(), CultureInfo.InvariantCulture);
        var authTime = time.GetUtcNow().AddSeconds(-age).ToUnixTimeSeconds();
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("auth_time", authTime.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var amr in Request.Headers["X-Test-Amr"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            claims.Add(new Claim("amr", amr));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Stands in for the identity provider: record what an OIDC challenge would send.
        var query = new List<string> { "returnUrl=" + Uri.EscapeDataString(properties.RedirectUri ?? "/") };
        foreach (var (key, value) in properties.Parameters)
        {
            // As the OIDC handler sends it: max_age in whole seconds.
            var text = value is TimeSpan span
                ? ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            query.Add(key + "=" + Uri.EscapeDataString(text));
        }

        Response.StatusCode = 302;
        Response.Headers.Location = "/test-idp/authorize?" + string.Join('&', query);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryStaffStore : IStaffStore
{
    private readonly ConcurrentDictionary<string, Staff> _rows = new(StringComparer.Ordinal);

    public void Put(Staff staff) => _rows[staff.Subject] = staff;

    public Task<Staff?> FindBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult(_rows.TryGetValue(subject, out var staff) ? staff : null);
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A fresh handler per client that forwards to the factory's fake, so disposing the
/// client's handler doesn't dispose the fake.</summary>
public sealed class FakeHandlerProxy(FakePaymentApi inner) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        inner.SendForTestAsync(request, cancellationToken);
}


public sealed class FakeOperationsMetrics : OpenPayments.Console.Data.IOperationsMetrics
{
    public OpenPayments.Console.Data.ThroughputSnapshot? Snapshot { get; set; } = new(
        CompletedPerSecond: 123.45,
        AdmittedPerSecond: 150,
        RejectedPerSecond: 2.5,
        OverheadP50: 0.004,
        OverheadP95: 0.018,
        OverheadP99: 0.042,
        PspWaitP99: 0.25,
        InFlight: 12,
        DeadlocksPerSecond: 0,
        PgBouncerMaxWait: 0.002,
        OutboxOldestAge: 3,
        TopRejectReason: "busy",
        TopRejectPerSecond: 2);

    public Task<OpenPayments.Console.Data.ThroughputSnapshot?> ReadAsync(CancellationToken ct) =>
        Task.FromResult(Snapshot);
}
