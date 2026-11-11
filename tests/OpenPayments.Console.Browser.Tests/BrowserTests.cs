using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenPayments.Console.Security;
using OpenPayments.Console.Tests;
using Xunit;

namespace OpenPayments.Console.Browser.Tests;

[CollectionDefinition("browser")]
public sealed class BrowserCollection : ICollectionFixture<ConsoleDb>, ICollectionFixture<Browsers>;

/// <summary>Chromium and WebKit, installed on first use (Playwright's own download).</summary>
public sealed class Browsers : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;

    public IBrowser Chromium { get; private set; } = null!;

    public IBrowser WebKit { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium", "webkit"]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Playwright browser install failed with {exit}.");
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Chromium = await Playwright.Chromium.LaunchAsync();
        WebKit = await Playwright.Webkit.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await Chromium.DisposeAsync();
        await WebKit.DisposeAsync();
        Playwright.Dispose();
    }
}

// Task C13: every screen, Chromium at 1280 px and WebKit at 390 px, light and dark.
[Collection("browser")]
public sealed class BrowserTests(ConsoleDb db, Browsers browsers)
{
    private static readonly string[] Screens = ["dashboard", "attention", "payments", "detail", "refund", "audit", "staff", "more"];

    public static TheoryData<string, string, int> ScreenMatrix()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var screen in Screens)
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                data.Add(screen, theme, 1280);
                if (screen != "staff")
                {
                    data.Add(screen, theme, 390);
                }
            }
        }

        return data;
    }

    public static TheoryData<string> PhoneScreens() => [.. Screens.Where(screen => screen != "staff")];

    [Theory]
    [MemberData(nameof(ScreenMatrix))]
    public async Task Each_screen_has_no_axe_violations(string page, string theme, int width)
    {
        await using var site = await SiteAsync();
        await using var context = await ContextAsync(site, width, theme == "dark" ? ColorScheme.Dark : ColorScheme.Light);
        var tab = await OpenAsync(context, site, page);

        var result = await tab.RunAxe();

        Assert.True(result.Violations.Length == 0,
            string.Join("\n", result.Violations.Select(v => $"{v.Id}: {v.Help} ({string.Join(", ", v.Nodes.Select(n => string.Join(" ", n.Target)))})")));
    }

    [Theory]
    [MemberData(nameof(PhoneScreens))]
    public async Task Each_screen_has_no_horizontal_scroll_at_390(string page)
    {
        await using var site = await SiteAsync();
        await using var context = await ContextAsync(site, 390, ColorScheme.Light);
        var tab = await OpenAsync(context, site, page);

        var overflow = await tab.EvaluateAsync<int>("() => document.documentElement.scrollWidth - window.innerWidth");

        Assert.True(overflow <= 0, $"{page} scrolls sideways by {overflow} px");
    }

    [Theory]
    [MemberData(nameof(PhoneScreens))]
    public async Task Touch_targets_are_at_least_44px_at_390(string page)
    {
        await using var site = await SiteAsync();
        await using var context = await ContextAsync(site, 390, ColorScheme.Light);
        var tab = await OpenAsync(context, site, page);

        var small = await tab.EvaluateAsync<string[]>(
            """
            () => [...document.querySelectorAll('a[href], button, input:not([type=hidden]), select, textarea')]
              .filter(el => { const s = getComputedStyle(el); const r = el.getBoundingClientRect();
                              return s.visibility !== 'hidden' && s.display !== 'none' && r.width > 0 && r.height > 0
                                     && r.right > 0 && r.left < window.innerWidth; })
              .filter(el => { const r = el.getBoundingClientRect(); return r.width < 44 || r.height < 44; })
              .map(el => `${el.tagName.toLowerCase()} "${(el.textContent || el.name || '').trim().slice(0, 30)}" ${Math.round(el.getBoundingClientRect().width)}x${Math.round(el.getBoundingClientRect().height)}`)
            """);

        Assert.True(small.Length == 0, page + ": " + string.Join("; ", small));
    }

    [Fact]
    public async Task Reduced_motion_removes_transitions()
    {
        await using var site = await SiteAsync();
        await using var context = await browsers.Chromium.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1280, Height = 900 },
            ReducedMotion = ReducedMotion.Reduce,
            ExtraHTTPHeaders = site.Headers,
        });
        var tab = await OpenAsync(context, site, "payments");

        var durations = await tab.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('.nav, .button, button')].map(el => getComputedStyle(el).transitionDuration)");

        Assert.NotEmpty(durations);
        Assert.All(durations, duration => Assert.Matches("^0s(, 0s)*$", duration));
    }

    [Fact]
    public async Task Attention_summary_and_first_row_fit_812_at_390()
    {
        await using var site = await SiteAsync();
        await using var context = await ContextAsync(site, 390, ColorScheme.Light);
        var tab = await context.NewPageAsync();
        await tab.GotoAsync(site.Url("/attention"));

        var bottom = await tab.EvaluateAsync<double>(
            """
            () => {
              const summary = document.querySelector('nav.summary');
              const first = document.querySelector('[data-row]');
              return Math.max(
                summary ? summary.getBoundingClientRect().bottom : 0,
                first ? first.getBoundingClientRect().bottom : 0);
            }
            """);

        Assert.True(bottom <= 812, $"Needs attention's first row ends at {bottom} px, past 812 px.");
    }

    [Fact]
    public async Task Double_tap_on_confirm_causes_one_action()
    {
        await using var site = await SiteAsync();
        await using var context = await ContextAsync(site, 390, ColorScheme.Light);
        var tab = await context.NewPageAsync();
        await tab.GotoAsync(site.Url($"/Payments/{site.Authorized}/Action/capture"));
        await tab.FillAsync("#reason", "Customer confirmed by phone");

        await tab.DblClickAsync("button[data-confirm]");
        await tab.WaitForURLAsync(url => url.Contains("fresh=1", StringComparison.Ordinal), new() { Timeout = 10000 });

        var keys = site.Factory.Api.Calls.Select(call => call.Key).Distinct().ToList();
        Assert.Single(keys);
    }

    private async Task<Site> SiteAsync()
    {
        await db.ResetAsync();
#pragma warning disable CA2000 // The Site owns the factory and disposes it.
        var factory = new DbConsoleFactory(db);
#pragma warning restore CA2000
        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        var staff = factory.AddStaff(ConsoleRole.Approver);
        var admin = factory.AddStaff(ConsoleRole.Admin);
        var captured = await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 125000);
        await db.SeedPaymentAsync("primary", "CAPTURED", amountMinor: 125000, id: captured);
        var authorized = await db.SeedPaymentAsync("primary", "AUTHORIZED", amountMinor: 4200);
        await db.SeedPaymentAsync("replica", "UNKNOWN", amountMinor: 990);
        // Needs attention gets rows in three queues, a role note and a badge.
        var review = await db.SeedPaymentAsync("replica", "REVIEW", amountMinor: 31050);
        var clock = factory.Clock.GetUtcNow();
        await db.ExecAsync("replica", $"""
            update payments set updated_at = '{clock.AddHours(-3):O}' where state = 'UNKNOWN';
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at, authorized_until)
            values (gen_random_uuid(), '{ConsoleDb.Merchant}', 'AUTHORIZED', 125000, 'USD', 0, now() + interval '1 day', now(), now(), '{clock.AddHours(9):O}');
            insert into console.review_resolutions (id, payment_id, kind, outcome, psp_answer, evidence, state, proposed_by)
            values (gen_random_uuid(), '{review}', 'payment', 'capture', 'Approved', 'PSP record', 'proposed', '{staff.Id}');
            """);
        await db.ExecAsync("primary", $"""
            insert into console.staff (id, oidc_subject, email, role) values
              ('{staff.Id}', '{staff.Subject}', 'approver@example.test', 'approver'),
              ('{admin.Id}', '{admin.Subject}', 'admin@example.test', 'admin');
            insert into console.audit_log (staff_id, phase, action, payment_id, idempotency_key, reason, result)
            values ('{staff.Id}', 'outcome', 'refund', '{captured}', 'k1', 'Damaged parcel', 'done: PARTIALLY_REFUNDED');
            """);
        return new Site(factory, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)), staff, admin, captured, authorized);
    }

    private async Task<IBrowserContext> ContextAsync(Site site, int width, ColorScheme scheme)
    {
        var browser = width < 768 ? browsers.WebKit : browsers.Chromium;
        return await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = width < 768 ? 844 : 900 },
            ColorScheme = scheme,
            ExtraHTTPHeaders = site.Headers,
            IgnoreHTTPSErrors = true,
        });
    }

    private static async Task<IPage> OpenAsync(IBrowserContext context, Site site, string page)
    {
        if (page == "staff")
        {
            await context.SetExtraHTTPHeadersAsync(site.AdminHeaders);
        }

        var tab = await context.NewPageAsync();
        var path = page switch
        {
            "dashboard" => "/",
            "attention" => "/attention",
            "payments" => "/Payments",
            "detail" => $"/Payments/{site.Captured}",
            "refund" => $"/Payments/{site.Captured}/Action/refund",
            "audit" => "/Audit",
            "staff" => "/Staff",
            "more" => "/More",
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        var response = await tab.GotoAsync(site.Url(path));
        Assert.True(response is { Ok: true }, $"{path} answered {response?.Status}");
        return tab;
    }

    private sealed record Site(DbConsoleFactory Factory, Uri BaseAddress, Staff Staff, Staff Admin, Guid Captured, Guid Authorized) : IAsyncDisposable
    {
        public Dictionary<string, string> Headers => Person(Staff);

        public Dictionary<string, string> AdminHeaders => Person(Admin);

        public string Url(string path) => new Uri(BaseAddress, path).ToString();

        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        private static Dictionary<string, string> Person(Staff staff) => new()
        {
            ["X-Test-Sub"] = staff.Subject,
            ["X-Test-Amr"] = "mfa",
            ["X-Test-Auth-Age"] = "30",
        };
    }
}
