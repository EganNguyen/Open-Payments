using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Actions;
using OpenPayments.Console.Data;
using OpenPayments.Console.Presentation;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Payments;

/// <summary>
/// Refund, capture, void and cancel. The order is fixed: policy and recent sign-in (attributes),
/// the form's key, the reason, then a fresh read of the primary to check the action is still
/// valid, then the audit intent, the API call with the form's key, and the audit outcome.
/// </summary>
[Authorize(Policy = ConsolePolicies.CanAct)]
[RequireRecentAuth]
public sealed class ActionModel(
    IConsoleReadStore store,
    IAuditWriter audit,
    PaymentApiClient api,
    FormKey formKeys,
    IOptions<SearchOptions> options,
    ILogger<ActionModel> logger) : PageModel
{
    private static readonly Dictionary<string, StaffAction> Known = new(StringComparer.Ordinal)
    {
        ["cancel"] = StaffAction.Cancel,
        ["capture"] = StaffAction.Capture,
        ["void"] = StaffAction.Void,
        ["refund"] = StaffAction.Refund,
    };

    [BindProperty(SupportsGet = true, Name = "id")] public Guid PaymentId { get; set; }

    [BindProperty(SupportsGet = true, Name = "action")] public string ActionName { get; set; } = string.Empty;

    [BindProperty] public string? Token { get; set; }

    [BindProperty] public string? Amount { get; set; }

    [BindProperty] public string? Reason { get; set; }

    /// <summary>"review" shows the confirmation; "confirm" acts.</summary>
    [BindProperty] public string? Step { get; set; }

    public PaymentRow Payment { get; private set; } = null!;

    public long AmountMinor { get; private set; }

    public string? AmountError { get; private set; }

    public string? ReasonError { get; private set; }

    public string? Notice { get; private set; }

    public string NoticeTone { get; private set; } = "warning";

    public bool Confirming { get; private set; }

    public bool ShowForm { get; private set; } = true;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!Known.TryGetValue(ActionName, out var action))
        {
            return NotFound();
        }

        var payment = await LoadAsync(ct);
        if (payment is null)
        {
            return NotFound();
        }

        Payment = payment;
        if (!StatePresentation.For(payment.State).Actions.Contains(action))
        {
            return Redirect(Back());
        }

        var restored = RequireRecentAuthAttribute.Restore(TempData);
        Token = restored.GetValueOrDefault("Token") ?? formKeys.Issue(PaymentId, ActionName, CurrentStaff.Get(HttpContext).Id);
        Reason = restored.GetValueOrDefault("Reason");
        Amount = restored.GetValueOrDefault("Amount") ?? (action == StaffAction.Refund ? MoneyFormat.Display(payment.RefundableMinor, payment.Currency) : null);
        AmountMinor = FixedAmount(action, payment);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // A cancelled browser request mustn't stop the outcome write, so the work below runs
        // without the request's token once the intent exists.
        var ct = HttpContext.RequestAborted;
        if (!Known.TryGetValue(ActionName, out var action))
        {
            return NotFound();
        }

        var staff = CurrentStaff.Get(HttpContext);
        if (!formKeys.TryRead(Token, PaymentId, ActionName, staff.Id, out var key))
        {
            return BadRequest("This form doesn't belong to this payment, action or person. Open the payment again.");
        }

        var payment = await LoadAsync(ct);
        if (payment is null)
        {
            return NotFound();
        }

        Payment = payment;
        AmountMinor = FixedAmount(action, payment);

        if (string.IsNullOrWhiteSpace(Reason))
        {
            ReasonError = "Write why you're doing this. It goes in the audit log.";
            return Page();
        }

        var alreadyStarted = await audit.IntentExistsAsync(key, ct);
        if (!alreadyStarted)
        {
            if (!StatePresentation.For(payment.State).Actions.Contains(action))
            {
                Notice = $"This payment changed to {StatePresentation.For(payment.State).Label} before you confirmed, so nothing was sent. Here is its current state.";
                ShowForm = false;
                return Page();
            }

            if (action == StaffAction.Refund)
            {
                if (!MoneyFormat.TryParse(Amount, payment.Currency, out var refund) || refund <= 0)
                {
                    AmountError = $"Enter an amount in {payment.Currency}, with at most {MoneyFormat.Exponent(payment.Currency)} decimal places.";
                    return Page();
                }

                if (refund > payment.RefundableMinor)
                {
                    AmountError = $"At most {MoneyFormat.DisplayWithCode(payment.RefundableMinor, payment.Currency)} can be refunded.";
                    return Page();
                }

                AmountMinor = refund;
            }

            if (Step != "confirm")
            {
                Confirming = true;
                return Page();
            }
        }
        else if (action == StaffAction.Refund && MoneyFormat.TryParse(Amount, payment.Currency, out var again))
        {
            AmountMinor = again;
        }

        if (!alreadyStarted)
        {
            try
            {
                await audit.WriteIntentAsync(
                    new AuditIntent(staff.Id, ActionName, payment.Id, null, AmountMinor, payment.Currency, Reason.Trim(), key),
                    ct);
            }
#pragma warning disable CA1031 // Any failure to record the intent stops the action.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "audit_intent_failed");
                Notice = "The action wasn't sent, because the audit record couldn't be written. Try again in a moment.";
                NoticeTone = "danger";
                Confirming = true;
                return Page();
            }
        }

        var answer = action switch
        {
            StaffAction.Capture => await api.CaptureAsync(payment.MerchantId, payment.Id, AmountMinor, key, CancellationToken.None),
            StaffAction.Void => await api.VoidAsync(payment.MerchantId, payment.Id, key, CancellationToken.None),
            StaffAction.Cancel => await api.CancelAsync(payment.MerchantId, payment.Id, key, CancellationToken.None),
            StaffAction.Refund => await api.RefundAsync(payment.MerchantId, payment.Id, AmountMinor, key, CancellationToken.None),
            _ => throw new InvalidOperationException(),
        };

        await audit.WriteOutcomeAsync(key, staff.Id, Describe(answer), CancellationToken.None);

        switch (answer)
        {
            case ApiAnswer.Done:
                return Redirect(Back() + "?fresh=1");
            case ApiAnswer.StillChecking:
                Notice = "Still checking. The payment service hasn't confirmed the result yet. Don't send it again: open the payment in a minute to see what happened.";
                ShowForm = false;
                return Page();
            case ApiAnswer.Conflict conflict:
                Payment = await LoadAsync(CancellationToken.None) ?? payment;
                Notice = $"The payment service refused this because the payment changed ({conflict.Reason}). It's now {StatePresentation.For(Payment.State).Label}. Nothing was retried.";
                ShowForm = false;
                return Page();
            case ApiAnswer.Busy busy:
                var wait = busy.RetryAfter is { } after ? $" Wait {Math.Max(1, (int)after.TotalSeconds)} s, then" : string.Empty;
                Notice = $"The payment service is busy.{wait} Send again: it uses the same key, so it can't happen twice.";
                Confirming = true;
                return Page();
            case ApiAnswer.Refused refused:
                Notice = $"The payment service didn't do this ({refused.Reason}).";
                NoticeTone = "danger";
                ShowForm = false;
                return Page();
            default:
                throw new InvalidOperationException();
        }
    }

    public string Back() => "/Payments/" + PaymentId.ToString("D");

    public string Effect()
    {
        var amount = MoneyFormat.DisplayWithCode(AmountMinor, Payment.Currency);
        return ActionName switch
        {
            "capture" => $"Capture {amount}. The reserved funds are taken from the customer.",
            "void" => $"Void the authorization of {MoneyFormat.DisplayWithCode(Payment.AmountMinor, Payment.Currency)}. The reservation is released and nothing is taken.",
            "cancel" => "Cancel this payment. No charge has been made, and none can be made after this.",
            "refund" => $"Refund {amount} to the customer.",
            _ => string.Empty,
        };
    }

    private async Task<PaymentRow?> LoadAsync(CancellationToken ct)
    {
        var payment = await store.FindPaymentAsync(PaymentId, ReadFrom.Primary, ct);
        return payment is not null && options.Value.Merchants.Contains(payment.MerchantId) ? payment : null;
    }

    private static long FixedAmount(StaffAction action, PaymentRow payment) => action switch
    {
        StaffAction.Capture => payment.AmountMinor,
        StaffAction.Refund => payment.RefundableMinor,
        _ => 0,
    };

    private static string Describe(ApiAnswer answer) => answer switch
    {
        ApiAnswer.Done done => "done: " + done.State,
        ApiAnswer.StillChecking => "still_checking",
        ApiAnswer.Conflict conflict => "conflict: " + conflict.Reason,
        ApiAnswer.Busy => "busy",
        ApiAnswer.Refused refused => "refused: " + refused.Reason,
        _ => "unknown",
    };
}
