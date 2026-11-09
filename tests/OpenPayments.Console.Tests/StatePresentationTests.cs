using OpenPayments.Console.Presentation;
using Xunit;

namespace OpenPayments.Console.Tests;

public sealed class StatePresentationTests
{
    // The spec's "Actions by state" table, copied verbatim. If either side changes, this fails.
    public static TheoryData<string, string, StateTone, StaffAction[], string> Spec() => new()
    {
        { "AWAITING_METHOD", "Waiting for payment", StateTone.Neutral, [StaffAction.Cancel], "No charge has been made." },
        { "PSP_PENDING", "Sent to PSP", StateTone.Neutral, [], "Waiting for the PSP to answer." },
        { "ACTION_REQUIRED", "Waiting for customer", StateTone.Neutral, [], "The customer needs to finish a step such as 3D Secure." },
        { "UNKNOWN", "Outcome unknown", StateTone.Warning, [], "We don't yet know if the customer was charged. We're checking automatically. Don't charge again." },
        { "REVIEW", "Needs review", StateTone.Danger, [StaffAction.ProposeResolution, StaffAction.Approve], "The PSP's answer disagrees with our record. A person must compare them." },
        { "AUTHORIZED", "Authorized", StateTone.Accent, [StaffAction.Capture, StaffAction.Void], "Funds are reserved, not taken." },
        { "CAPTURED", "Captured", StateTone.Success, [StaffAction.Refund], "Funds were taken from the customer." },
        { "PARTIALLY_REFUNDED", "Partly refunded", StateTone.Success, [StaffAction.Refund], "Part of the captured amount was returned." },
        { "REFUNDED", "Refunded", StateTone.Neutral, [], "The full captured amount was returned." },
        { "CANCELED", "Canceled", StateTone.Neutral, [], "Ended with no successful attempt. No charge was made." },
        { "VOIDED", "Voided", StateTone.Neutral, [], "The reservation was released before capture." },
        { "EXPIRED", "Expired", StateTone.Neutral, [], "The authorization window ended without capture." },
    };

    [Theory]
    [MemberData(nameof(Spec))]
    public void Every_committed_state_matches_spec(string state, string label, StateTone tone, StaffAction[] actions, string wording)
    {
        var view = StatePresentation.For(state);
        Assert.Equal(label, view.Label);
        Assert.Equal(tone, view.Tone);
        Assert.Equal(actions, view.Actions);
        Assert.Equal(wording, view.Wording);
    }

    [Fact]
    public void The_table_has_exactly_the_twelve_committed_states() =>
        Assert.Equal(12, StatePresentation.CommittedStates.Count);

    [Fact]
    public void Unknown_state_says_do_not_charge_again_and_offers_no_action()
    {
        var view = StatePresentation.For("UNKNOWN");
        Assert.Contains("Don't charge again.", view.Wording, StringComparison.Ordinal);
        Assert.Empty(view.Actions);
    }

    [Fact]
    public void No_state_offers_retry_or_new_attempt()
    {
        var names = Enum.GetNames<StaffAction>();
        Assert.DoesNotContain(names, name => name.Contains("Retry", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Attempt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unrecognized_state_shows_raw_name_and_no_actions()
    {
        var view = StatePresentation.For("PARTIALLY_CAPTURED");
        Assert.Equal("PARTIALLY_CAPTURED", view.Label);
        Assert.Equal(StateTone.Neutral, view.Tone);
        Assert.Empty(view.Actions);
    }

    [Fact]
    public void Stage_1_review_shows_no_resolution_controls()
    {
        var view = StatePresentation.For("REVIEW");
        Assert.Empty(StatePresentation.Shown(view, canAct: true, resolutionEnabled: false));
    }
}
