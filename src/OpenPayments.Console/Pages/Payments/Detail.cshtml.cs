using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenPayments.Console.Data;
using OpenPayments.Console.Presentation;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Payments;

public sealed class DetailModel(
    IConsoleReadStore store,
    ITimelineReader timeline,
    IResolutionReader resolutions,
    IAuthorizationService authorization,
    IOptions<SearchOptions> options,
    TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)] public int Fresh { get; set; }

    /// <summary>N3: which Needs attention queue sent the person here.</summary>
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    public string? CasePanel { get; private set; }

    public bool Missing { get; private set; }

    public PaymentRow Payment { get; private set; } = null!;

    public PaymentDetail Detail { get; private set; } = null!;

    public StateView View { get; private set; } = null!;

    public IReadOnlyList<StaffAction> Actions { get; private set; } = [];

    public Guid? OpenProposalId { get; private set; }

    public TimeSpan? Lag { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public long PendingRefundMinor => Detail.Refunds.Where(r => r.Unresolved).Sum(r => r.AmountMinor);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        // After an action, ?fresh=1 reads the primary so staff never see stale data.
        var from = Fresh == 1 ? ReadFrom.Primary : ReadFrom.Replica;
        PaymentRow? payment = null;
        if (from == ReadFrom.Replica)
        {
            try
            {
                payment = await store.FindPaymentAsync(Id, ReadFrom.Replica, ct);
            }
            catch (ReplicaUnavailableException)
            {
            }

            if (payment is null)
            {
                from = ReadFrom.Primary;
            }
            else
            {
                Lag = await store.ReplicaLagAsync(ct);
            }
        }

        payment ??= await store.FindPaymentAsync(Id, ReadFrom.Primary, ct);
        if (payment is null || !options.Value.Merchants.Contains(payment.MerchantId))
        {
            Missing = true;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        Payment = payment;
        CasePanel = From switch
        {
            "review" => "This case needs review: compare our record with the PSP's answer, then propose a resolution.",
            "approval" => "A proposed resolution is waiting for a different approver.",
            "unresolved" => "The outcome is unknown. We're checking automatically; don't start a new charge.",
            "expiring" => "This authorization ends soon. Capture or void it before the PSP expires it.",
            "orphan" => "An action has no recorded result. Check the PSP before acting again.",
            _ => null,
        };
        Detail = await timeline.ReadAsync(payment.Id, payment.Currency, from, ct);
        View = StatePresentation.For(payment.State);
        var canAct = (await authorization.AuthorizeAsync(User, ConsolePolicies.CanAct)).Succeeded;
        if (payment.State == "REVIEW")
        {
            OpenProposalId = (await resolutions.FindCaseAsync(payment.Id, null, null, ct))?.OpenProposalId;
        }

        Actions = StatePresentation.Shown(View, canAct, resolutionEnabled: true)
            .Where(action => action != StaffAction.Approve || OpenProposalId is not null)
            .ToList();
        return Page();
    }

    public static string ActionName(StaffAction action) => action switch
    {
        StaffAction.Cancel => "cancel",
        StaffAction.Capture => "capture",
        StaffAction.Void => "void",
        StaffAction.Refund => "refund",
        StaffAction.ProposeResolution => "propose",
        StaffAction.Approve => "approve",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string ActionLabel(StaffAction action, string state) => action switch
    {
        StaffAction.Cancel => "Cancel payment",
        StaffAction.Capture => "Capture in full",
        StaffAction.Void => "Void",
        StaffAction.Refund => state == "PARTIALLY_REFUNDED" ? "Refund the remainder or part of it" : "Refund",
        StaffAction.ProposeResolution => "Propose resolution",
        StaffAction.Approve => "Approve",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
