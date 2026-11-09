namespace OpenPayments.Console.Presentation;

public enum StateTone
{
    Neutral,
    Warning,
    Danger,
    Accent,
    Success,
}

public enum StaffAction
{
    Cancel,
    Capture,
    Void,
    Refund,
    ProposeResolution,
    Approve,
}

public sealed record StateView(string Label, StateTone Tone, string Icon, string Wording, IReadOnlyList<StaffAction> Actions)
{
    public string ToneClass => "tone-" + Tone.ToString().ToLowerInvariant();
}

/// <summary>
/// The spec's "Actions by state" table, verbatim, and the only place those rows live: pages and
/// tests both read it. Staff never start a new attempt, and UNKNOWN offers nothing.
/// </summary>
public static class StatePresentation
{
    private static readonly Dictionary<string, StateView> States = new(StringComparer.Ordinal)
    {
        ["AWAITING_METHOD"] = new("Waiting for payment", StateTone.Neutral, "clock", "No charge has been made.", [StaffAction.Cancel]),
        ["PSP_PENDING"] = new("Sent to PSP", StateTone.Neutral, "clock", "Waiting for the PSP to answer.", []),
        ["ACTION_REQUIRED"] = new("Waiting for customer", StateTone.Neutral, "clock", "The customer needs to finish a step such as 3D Secure.", []),
        ["UNKNOWN"] = new("Outcome unknown", StateTone.Warning, "alert", "We don't yet know if the customer was charged. We're checking automatically. Don't charge again.", []),
        ["REVIEW"] = new("Needs review", StateTone.Danger, "stop", "The PSP's answer disagrees with our record. A person must compare them.", [StaffAction.ProposeResolution, StaffAction.Approve]),
        ["AUTHORIZED"] = new("Authorized", StateTone.Accent, "hold", "Funds are reserved, not taken.", [StaffAction.Capture, StaffAction.Void]),
        ["CAPTURED"] = new("Captured", StateTone.Success, "check", "Funds were taken from the customer.", [StaffAction.Refund]),
        ["PARTIALLY_REFUNDED"] = new("Partly refunded", StateTone.Success, "check", "Part of the captured amount was returned.", [StaffAction.Refund]),
        ["REFUNDED"] = new("Refunded", StateTone.Neutral, "dot", "The full captured amount was returned.", []),
        ["CANCELED"] = new("Canceled", StateTone.Neutral, "dot", "Ended with no successful attempt. No charge was made.", []),
        ["VOIDED"] = new("Voided", StateTone.Neutral, "dot", "The reservation was released before capture.", []),
        ["EXPIRED"] = new("Expired", StateTone.Neutral, "dot", "The authorization window ended without capture.", []),
    };

    public static IReadOnlyCollection<string> CommittedStates => States.Keys;

    public static StateView For(string rawState) =>
        States.TryGetValue(rawState, out var view)
            ? view
            : new StateView(rawState, StateTone.Neutral, "dot", string.Empty, []);

    /// <summary>The actions a page shows: the state's actions the viewer's role may take.
    /// In Stage 1 a REVIEW case shows no resolution controls until C12.</summary>
    public static IReadOnlyList<StaffAction> Shown(StateView view, bool canAct, bool resolutionEnabled) =>
        canAct
            ? view.Actions.Where(action => resolutionEnabled || action is not (StaffAction.ProposeResolution or StaffAction.Approve)).ToList()
            : [];
}
