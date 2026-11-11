using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenPayments.Console.Actions;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Review;

/// <summary>C12: a different approver confirms or rejects a proposal.</summary>
[Authorize(Policy = ConsolePolicies.CanApprove)]
[RequireRecentAuth]
public sealed class DecideModel(
    IResolutionReader reader,
    IResolutionWriter writer,
    PaymentApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "id")] public Guid Id { get; set; }

    [BindProperty] public string? Decision { get; set; }

    [BindProperty] public string? Reason { get; set; }

    public ProposalView? Proposal { get; private set; }

    public bool OwnProposal { get; private set; }

    public string? Notice { get; private set; }

    public string NoticeTone { get; private set; } = "warning";

    public bool Decided => Proposal?.State != "proposed";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }

        if (Proposal!.State != "proposed")
        {
            return Page();
        }

        var person = CurrentStaff.Get(HttpContext);
        if (Decision == "reject")
        {
            if (string.IsNullOrWhiteSpace(Reason))
            {
                Notice = "A reason is required to reject.";
                return Page();
            }

            if (await writer.RejectAsync(Id, person.Id, Reason, ct))
            {
                TempData["Notice"] = "The proposal was rejected.";
                return RedirectToPage("/Attention/Index");
            }

            Notice = "The proposal is no longer open.";
            await LoadAsync(ct);
            return Page();
        }

        if (OwnProposal)
        {
            Notice = "You proposed this; a different approver must decide it.";
            return Page();
        }

        var answer = await api.ApplyResolutionAsync(Id, person.Id, ct);
        switch (answer)
        {
            case ApiAnswer.Done done:
                Notice = "Applied. The payment is now " + done.State + ".";
                NoticeTone = "success";
                break;
            case ApiAnswer.Conflict conflict:
                Notice = "The payment changed while this was open: " + conflict.Reason + ".";
                break;
            case ApiAnswer.Refused refused when refused.Reason == "self_approval":
                Notice = "You proposed this; a different approver must decide it.";
                break;
            case ApiAnswer.Refused refused:
                Notice = "The API refused it: " + refused.Reason + ".";
                break;
            case ApiAnswer.StillChecking:
                Notice = "The outcome isn't known yet. Reload before doing anything else.";
                break;
            case ApiAnswer.Busy:
                Notice = "The API is busy. Try again in a moment.";
                break;
        }

        await LoadAsync(ct);
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        Proposal = await reader.FindProposalAsync(Id, ct);
        if (Proposal is null)
        {
            return false;
        }

        OwnProposal = Proposal.ProposedBy == CurrentStaff.Get(HttpContext).Id;
        if (Notice is null && TempData["Notice"] is string notice)
        {
            Notice = notice;
        }

        return true;
    }
}
