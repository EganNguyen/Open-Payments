using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace OpenPayments.Console.Security;

/// <summary>
/// Money actions need a sign-in within the re-authentication window. A POST with an older
/// auth_time doesn't run: its form values are kept in TempData (encrypted, never in the URL)
/// and the person is sent through the identity provider with prompt=login and max_age, then
/// back to the same form, which restores the values.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireRecentAuthAttribute : Attribute, IAsyncPageFilter
{
    public const string RestoreKey = "console.reauth.form";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            await next();
            return;
        }

        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<ConsoleSecurityOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        if (IsRecent(http.User, time.GetUtcNow(), options.ReauthWindow))
        {
            await next();
            return;
        }

        var form = http.Request.HasFormContentType
            ? http.Request.Form
                .Where(field => field.Key != "__RequestVerificationToken")
                .ToDictionary(field => field.Key, field => field.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var tempData = services.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
        tempData[RestoreKey] = JsonSerializer.Serialize(form);
        tempData.Save();

        var properties = new OpenIdConnectChallengeProperties
        {
            RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString,
            Prompt = "login",
            MaxAge = options.ReauthWindow,
        };
        context.Result = new ChallengeResult(properties);
    }

    public static bool IsRecent(System.Security.Claims.ClaimsPrincipal user, DateTimeOffset now, TimeSpan window)
    {
        var value = user.FindFirst("auth_time")?.Value;
        if (value is null || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return age >= TimeSpan.Zero - TimeSpan.FromMinutes(1) && age <= window;
    }

    /// <summary>The values a re-authentication kept for this form, if any.</summary>
    public static IReadOnlyDictionary<string, string> Restore(ITempDataDictionary tempData)
    {
        if (tempData[RestoreKey] is string json
            && JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } values)
        {
            return values;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
