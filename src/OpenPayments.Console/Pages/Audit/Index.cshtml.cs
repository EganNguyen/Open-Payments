using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;

namespace OpenPayments.Console.Pages.Audit;

/// <summary>Who did what, when and why. Read-only: the page has no edit or delete, and no role
/// can change the table. Stage 1 marks intents with no outcome here, until C12's queue.</summary>
public sealed class IndexModel(IAdminStore store, IOptions<SearchOptions> options, IOptions<AuditOptions> audit, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    [BindProperty(SupportsGet = true)] public string? To { get; set; }

    [BindProperty(SupportsGet = true)] public Guid? Staff { get; set; }

    [BindProperty(SupportsGet = true)] public Guid? Payment { get; set; }

    [BindProperty(SupportsGet = true)] public long? Before { get; set; }

    public AuditPage? Results { get; private set; }

    public string? Message { get; private set; }

    public DateOnly FromDate { get; private set; }

    public DateOnly ToDate { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        ToDate = Parse(To) ?? today;
        FromDate = Parse(From) ?? ToDate.AddDays(-(int)options.Value.DefaultRange.TotalDays + 1);
        var from = new DateTimeOffset(FromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (to <= from || to - from > options.Value.MaxRange)
        {
            Message = $"Choose a range of {(int)options.Value.MaxRange.TotalDays} days or less.";
            return;
        }

        Results = await store.ReadAuditAsync(new AuditQuery(from, to, Staff, Payment, Before), audit.Value.IntentLimit, ct);
    }

    public string NextLink(long before) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/Audit", new Dictionary<string, string?>
    {
        ["From"] = FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["To"] = ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["Staff"] = Staff?.ToString("D"),
        ["Payment"] = Payment?.ToString("D"),
        ["Before"] = before.ToString(CultureInfo.InvariantCulture),
    }.Where(pair => pair.Value is not null));

    private static DateOnly? Parse(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}

public sealed class AuditOptions
{
    /// <summary>An intent with no outcome after this long is flagged.</summary>
    public TimeSpan IntentLimit { get; set; } = TimeSpan.FromMinutes(10);
}
