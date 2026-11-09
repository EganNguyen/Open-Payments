using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;
using OpenPayments.Console.Presentation;

namespace OpenPayments.Console.Pages.Payments;

public sealed class IndexModel(IConsoleReadStore store, IOptions<SearchOptions> options, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    [BindProperty(SupportsGet = true)] public string? To { get; set; }

    [BindProperty(SupportsGet = true)] public string? State { get; set; }

    [BindProperty(SupportsGet = true)] public string? Currency { get; set; }

    [BindProperty(SupportsGet = true)] public string? Min { get; set; }

    [BindProperty(SupportsGet = true)] public string? Max { get; set; }

    [BindProperty(SupportsGet = true)] public string? After { get; set; }

    public PaymentPage? Results { get; private set; }

    public string? Message { get; private set; }

    public bool SearchUnavailable { get; private set; }

    public TimeSpan? Lag { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public DateOnly FromDate { get; private set; }

    public DateOnly ToDate { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        var settings = options.Value;

        // A pasted ID goes straight to the payment: replica first, then the primary, so a payment
        // the replica hasn't received yet still opens.
        if (!string.IsNullOrWhiteSpace(Q))
        {
            if (!Guid.TryParse(Q.Trim(), out var id))
            {
                Message = "Paste a full payment ID, or search by date below.";
            }
            else
            {
                PaymentRow? found = null;
                try
                {
                    found = await store.FindPaymentAsync(id, ReadFrom.Replica, ct);
                }
                catch (ReplicaUnavailableException)
                {
                }

                found ??= await store.FindPaymentAsync(id, ReadFrom.Primary, ct);
                if (found is not null && settings.Merchants.Contains(found.MerchantId))
                {
                    return Redirect("/Payments/" + found.Id.ToString("D"));
                }

                Message = "No payment has that ID.";
            }
        }

        Lag = await store.ReplicaLagAsync(ct);
        if (Lag is null)
        {
            SearchUnavailable = true;
            Message ??= "Search is unavailable because the read replica can't be reached. Looking up a payment by ID still works.";
            return Page();
        }

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        ToDate = ParseDate(To) ?? today;
        FromDate = ParseDate(From) ?? ToDate.AddDays(-(int)settings.DefaultRange.TotalDays + 1);
        var fromAt = new DateTimeOffset(FromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toAt = new DateTimeOffset(ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (toAt <= fromAt)
        {
            Message = "The start date must be on or before the end date.";
            return Page();
        }

        if (toAt - fromAt > settings.MaxRange)
        {
            Message = $"Choose a range of {(int)settings.MaxRange.TotalDays} days or less.";
            return Page();
        }

        var state = string.IsNullOrEmpty(State) ? null : State;
        if (state is not null && !SearchOptions.FilterStates.Contains(state))
        {
            state = null;
        }

        long? min = null, max = null;
        var currency = string.IsNullOrEmpty(Currency) ? "USD" : Currency;
        if (!string.IsNullOrWhiteSpace(Min) || !string.IsNullOrWhiteSpace(Max))
        {
            if (!MoneyFormat.Exponents.ContainsKey(currency))
            {
                Message = "Choose a currency for the amount filter.";
                return Page();
            }

            if (!string.IsNullOrWhiteSpace(Min))
            {
                if (!MoneyFormat.TryParse(Min, currency, out var value))
                {
                    Message = "The minimum amount isn't a valid amount for " + currency + ".";
                    return Page();
                }

                min = value;
            }

            if (!string.IsNullOrWhiteSpace(Max))
            {
                if (!MoneyFormat.TryParse(Max, currency, out var value))
                {
                    Message = "The maximum amount isn't a valid amount for " + currency + ".";
                    return Page();
                }

                max = value;
            }
        }

        try
        {
            Results = await store.ListPaymentsAsync(
                new PaymentQuery(settings.Merchants, fromAt, toAt, state, min, max, After),
                ct);
        }
        catch (ReadTimeoutException)
        {
            Message = "The search took too long. Narrow the date range and try again.";
        }
        catch (ReplicaUnavailableException)
        {
            SearchUnavailable = true;
            Message = "Search is unavailable because the read replica can't be reached. Looking up a payment by ID still works.";
        }

        return Page();
    }

    public string NextLink(string cursor)
    {
        var query = new Dictionary<string, string?>
        {
            ["From"] = FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["To"] = ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["State"] = State,
            ["Currency"] = Currency,
            ["Min"] = Min,
            ["Max"] = Max,
            ["After"] = cursor,
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/Payments", query.Where(p => !string.IsNullOrEmpty(p.Value)));
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
