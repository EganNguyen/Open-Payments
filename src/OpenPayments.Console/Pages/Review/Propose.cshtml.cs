using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenPayments.Console.Data;
using OpenPayments.Console.Presentation;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Pages.Review;

/// <summary>
/// C12: record the PSP's answer and evidence for a REVIEW case. It only writes the proposal;
/// money moves when a different approver applies it through the internal route.
/// </summary>
[Authorize(Policy = ConsolePolicies.CanAct)]
[RequireRecentAuth]
public sealed class ProposeModel(
    IResolutionReader reader,
    IResolutionWriter writer) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "id")] public Guid PaymentId { get; set; }

    [BindProperty(SupportsGet = true, Name = "attempt")] public Guid? AttemptId { get; set; }

    [BindProperty(SupportsGet = true, Name = "refund")] public Guid? RefundId { get; set; }

    [BindProperty(SupportsGet = true, Name = "kind")] public string Kind { get; set; } = "payment";

    [BindProperty] public string? Outcome { get; set; }

    [BindProperty] public string? PspAnswer { get; set; }

    [BindProperty] public string? Evidence { get; set; }

    public ReviewCase? Case { get; private set; }

    public IReadOnlyList<OutcomeChoice> Outcomes => ResolutionChoices.For(Kind, Case?.AttemptOperation);

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Case = await reader.FindCaseAsync(PaymentId, AttemptId, RefundId, ct);
        if (Case is null)
        {
            return NotFound();
        }

        if (Case.OpenProposalId is { } open)
        {
            return RedirectToPage("Decide", new { id = open });
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!TryParseKind(Kind, out var kind))
        {
            Error = "Choose what this case is.";
            return await ReloadAsync(ct);
        }

        if (!TryParseOutcome(Outcome, out var outcome)
            || !ResolutionChoices.Values(kind, null).Contains(ResolutionWriter.OutcomeText(outcome)))
        {
            Error = "Choose the PSP's result.";
            return await ReloadAsync(ct);
        }

        var person = CurrentStaff.Get(HttpContext);
        var result = await writer.ProposeAsync(
            new NewResolution(PaymentId, AttemptId, RefundId, kind, outcome, PspAnswer ?? string.Empty,
                Evidence ?? string.Empty, person.Id),
            ct);
        return result switch
        {
            ProposalResult.Created created => RedirectToPage("Decide", new { id = created.Id }),
            ProposalResult.AlreadyOpen open => RedirectToPage("Decide", new { id = open.ExistingId }),
            ProposalResult.Invalid invalid => await ReloadAsync(ct, invalid.Reason),
            _ => await ReloadAsync(ct),
        };
    }

    private async Task<IActionResult> ReloadAsync(CancellationToken ct, string? error = null)
    {
        Error = error;
        Case = await reader.FindCaseAsync(PaymentId, AttemptId, RefundId, ct);
        return Case is null ? NotFound() : Page();
    }

    private static bool TryParseKind(string value, out ResolutionKind kind)
    {
        switch (value)
        {
            case "payment": kind = ResolutionKind.Payment; return true;
            case "refund": kind = ResolutionKind.Refund; return true;
            case "late_reversal": kind = ResolutionKind.LateReversal; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryParseOutcome(string? value, out ResolutionOutcome outcome)
    {
        switch (value)
        {
            case "authorize": outcome = ResolutionOutcome.Authorize; return true;
            case "awaiting_method": outcome = ResolutionOutcome.AwaitingMethod; return true;
            case "capture": outcome = ResolutionOutcome.Capture; return true;
            case "confirm": outcome = ResolutionOutcome.Confirm; return true;
            case "release": outcome = ResolutionOutcome.Release; return true;
            case "reversed_at_psp": outcome = ResolutionOutcome.ReversedAtPsp; return true;
            default: outcome = default; return false;
        }
    }
}

public sealed record OutcomeChoice(string Value, string Label);

/// <summary>The C3 attempt and refund tables, in the one place the pages read them.</summary>
public static class ResolutionChoices
{
    public static IReadOnlyList<string> Values(ResolutionKind kind, string? operation) => kind switch
    {
        ResolutionKind.Payment when operation == "sale" => ["awaiting_method", "capture"],
        ResolutionKind.Payment => ["authorize", "awaiting_method", "capture"],
        ResolutionKind.Refund => ["confirm", "release"],
        ResolutionKind.LateReversal => ["reversed_at_psp"],
        _ => [],
    };

    public static IReadOnlyList<OutcomeChoice> For(string kind, string? operation)
    {
        if (!Enum.TryParse<ResolutionKind>(Pascal(kind), out var parsed))
        {
            return [];
        }

        return [.. Values(parsed, operation).Select(value => new OutcomeChoice(value, Label(value)))];
    }

    public static string Label(string value) => value switch
    {
        "authorize" => "Record an authorization",
        "awaiting_method" => "Send the payment back to await a method",
        "capture" => "Record a capture",
        "confirm" => "Confirm the refund",
        "release" => "Release the reserved refund",
        "reversed_at_psp" => "Reverse this at the PSP first",
        _ => value,
    };

    private static string Pascal(string value) => value switch
    {
        "payment" => nameof(ResolutionKind.Payment),
        "refund" => nameof(ResolutionKind.Refund),
        "late_reversal" => nameof(ResolutionKind.LateReversal),
        _ => value,
    };
}
