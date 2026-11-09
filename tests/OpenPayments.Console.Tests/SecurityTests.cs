using System.Net;
using System.Text.RegularExpressions;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C6: sign-in, roles and the page rules every later screen relies on.
public sealed partial class SecurityTests(ConsoleFactory factory) : IClassFixture<ConsoleFactory>
{
    [Fact]
    public async Task Session_without_mfa_claim_is_refused()
    {
        var staff = factory.AddStaff(ConsoleRole.Admin);
        using var client = factory.ClientFor(staff.Subject, amr: "pwd");

        var response = await client.GetAsync("/Payments");

        // The test scheme answers a failed policy with 403; the cookie scheme sends /Account/Denied.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_subject_is_refused()
    {
        using var client = factory.ClientFor("nobody-" + Guid.NewGuid());

        var response = await client.GetAsync("/Payments");

        // The test scheme answers a failed policy with 403; the cookie scheme sends /Account/Denied.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_staff_is_refused_on_their_next_request()
    {
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var client = factory.ClientFor(staff.Subject);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Payments")).StatusCode);

        factory.Staff.Put(staff with { DisabledAt = factory.Clock.GetUtcNow() });

        var response = await client.GetAsync("/Payments");
        // The test scheme answers a failed policy with 403; the cookie scheme sends /Account/Denied.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Role_change_applies_on_next_request_with_no_new_login()
    {
        var staff = factory.AddStaff(ConsoleRole.Viewer);
        using var client = factory.ClientFor(staff.Subject);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test/policy/" + ConsolePolicies.CanAdmin)).StatusCode);

        factory.Staff.Put(staff with { Role = ConsoleRole.Admin });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/policy/" + ConsolePolicies.CanAdmin)).StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_is_rejected()
    {
        var staff = factory.AddStaff(ConsoleRole.Viewer);
        using var client = factory.ClientFor(staff.Subject);

        using var body = new FormUrlEncodedContent([]);
        var response = await client.PostAsync("/Account/SignOut", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pages_reference_only_same_origin_assets()
    {
        var staff = factory.AddStaff(ConsoleRole.Admin);
        using var client = factory.ClientFor(staff.Subject);

        foreach (var path in new[] { "/Payments", "/More", "/Account/Denied" })
        {
            var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            foreach (Match match in AssetReference().Matches(html))
            {
                var url = match.Groups["url"].Value;
                Assert.False(
                    url.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal),
                    $"{path} references another origin: {url}");
            }

            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
            foreach (var directive in csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.DoesNotContain("http", directive, StringComparison.Ordinal);
                Assert.DoesNotContain("*", directive, StringComparison.Ordinal);
                Assert.DoesNotContain("unsafe-inline", directive, StringComparison.Ordinal);
            }
        }
    }

    public static TheoryData<ConsoleRole, string, bool> RoleTable() => new()
    {
        { ConsoleRole.Viewer, ConsolePolicies.CanRead, true },
        { ConsoleRole.Viewer, ConsolePolicies.CanAct, false },
        { ConsoleRole.Viewer, ConsolePolicies.CanApprove, false },
        { ConsoleRole.Viewer, ConsolePolicies.CanAdmin, false },
        { ConsoleRole.Operator, ConsolePolicies.CanRead, true },
        { ConsoleRole.Operator, ConsolePolicies.CanAct, true },
        { ConsoleRole.Operator, ConsolePolicies.CanApprove, false },
        { ConsoleRole.Operator, ConsolePolicies.CanAdmin, false },
        { ConsoleRole.Approver, ConsolePolicies.CanRead, true },
        { ConsoleRole.Approver, ConsolePolicies.CanAct, true },
        { ConsoleRole.Approver, ConsolePolicies.CanApprove, true },
        { ConsoleRole.Approver, ConsolePolicies.CanAdmin, false },
        { ConsoleRole.Admin, ConsolePolicies.CanRead, true },
        { ConsoleRole.Admin, ConsolePolicies.CanAct, false },
        { ConsoleRole.Admin, ConsolePolicies.CanApprove, false },
        { ConsoleRole.Admin, ConsolePolicies.CanAdmin, true },
    };

    [Theory]
    [MemberData(nameof(RoleTable))]
    public async Task Every_policy_matches_the_role_table(ConsoleRole role, string policy, bool allowed)
    {
        Assert.Equal(allowed, ConsolePolicies.Allows(role, policy));

        // And the server enforces it: each policy guards a probe endpoint in Testing.
        var staff = factory.AddStaff(role);
        using var client = factory.ClientFor(staff.Subject);
        var response = await client.GetAsync("/test/policy/" + policy);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [GeneratedRegex("""(?:src|href|action)\s*=\s*"(?<url>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AssetReference();
}
