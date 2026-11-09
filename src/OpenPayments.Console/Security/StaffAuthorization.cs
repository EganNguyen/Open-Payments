using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace OpenPayments.Console.Security;

public sealed class ConsoleSecurityOptions
{
    /// <summary>The amr value that shows MFA was used. Providers differ, so it is configurable.</summary>
    public string RequiredAmr { get; set; } = "mfa";

    /// <summary>D55: an acr value that also shows MFA, for providers that report a step-up level
    /// instead of amr. Empty keeps the amr-only rule.</summary>
    public string RequiredAcr { get; set; } = string.Empty;

    public TimeSpan ReauthWindow { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record StaffRequirement(string Policy) : IAuthorizationRequirement;

/// <summary>
/// Every policy resolves the signed-in person from console.staff on each request. A session
/// without MFA, an unknown subject or a disabled person fails every policy.
/// </summary>
public sealed class StaffAuthorizationHandler(
    IStaffStore staff,
    IHttpContextAccessor http,
    IOptions<ConsoleSecurityOptions> options) : AuthorizationHandler<StaffRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StaffRequirement requirement)
    {
        var person = await CurrentStaff.ResolveAsync(http.HttpContext, context.User, staff, options.Value);
        if (person is not null && ConsolePolicies.Allows(person.Role, requirement.Policy))
        {
            context.Succeed(requirement);
        }
    }
}

public static class CurrentStaff
{
    private const string ItemKey = "console.staff";

    public static string? Subject(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool HasMfa(ClaimsPrincipal user, ConsoleSecurityOptions options) =>
        user.FindAll("amr").Any(claim => string.Equals(claim.Value, options.RequiredAmr, StringComparison.Ordinal))
        || (options.RequiredAcr.Length > 0
            && string.Equals(user.FindFirstValue("acr"), options.RequiredAcr, StringComparison.Ordinal));

    /// <summary>The active staff row for this request, looked up once per request.</summary>
    public static async Task<Staff?> ResolveAsync(
        HttpContext? context,
        ClaimsPrincipal user,
        IStaffStore store,
        ConsoleSecurityOptions options)
    {
        if (context is not null && context.Items.TryGetValue(ItemKey, out var cached))
        {
            return cached as Staff;
        }

        Staff? result = null;
        string? refusal = null;
        var subject = Subject(user);
        if (user.Identity?.IsAuthenticated != true || subject is null)
        {
            refusal = "not_signed_in";
        }
        else if (!HasMfa(user, options))
        {
            refusal = "no_mfa";
        }
        else
        {
            var row = await store.FindBySubjectAsync(subject, context?.RequestAborted ?? CancellationToken.None);
            if (row is null)
            {
                refusal = "unknown_subject";
            }
            else if (row.DisabledAt is not null)
            {
                refusal = "disabled";
            }
            else
            {
                result = row;
            }
        }

        if (refusal is not null && context is not null && user.Identity?.IsAuthenticated == true)
        {
            // Claim types and the MFA claims only: enough to see why, with no personal data.
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenPayments.Console.Security")
                .LogWarning(
                    "staff_refused reason={Reason} amr={Amr} acr={Acr} claims={Claims}",
                    refusal,
                    string.Join(",", user.FindAll("amr").Select(c => c.Value)),
                    user.FindFirstValue("acr") ?? "none",
                    string.Join(",", user.Claims.Select(c => c.Type).Distinct()));
        }

        if (context is not null)
        {
            context.Items[ItemKey] = result;
        }

        return result;
    }

    /// <summary>The staff member already resolved for this request, or null on pages that
    /// allow anonymous access.</summary>
    public static Staff? Find(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as Staff : null;

    public static Staff Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is Staff staff
            ? staff
            : throw new InvalidOperationException("No authorized staff member on this request.");
}
