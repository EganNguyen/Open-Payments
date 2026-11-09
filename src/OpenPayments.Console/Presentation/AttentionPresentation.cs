using System.Globalization;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Presentation;

public sealed record QueueText(string Key, string Title, string Help);

/// <summary>
/// The words on Needs attention: queue titles and help from the prototype, and one plain
/// sentence per row. The page and its tests both read this.
/// </summary>
public static class AttentionPresentation
{
    public static QueueText For(Queue queue) => queue switch
    {
        Queue.Review => new("review", "Needs review", "The PSP's answer disagrees with our record, on a payment, refund or attempt."),
        Queue.Approval => new("approval", "Waiting for approval", "Someone proposed a resolution. A second person must decide."),
        Queue.Orphan => new("orphan", "Actions with no recorded result", "A staff action started, but its result was never saved. Open each payment and check it."),
        Queue.Unresolved => new("unresolved", "Unresolved too long", "We're checking automatically. Nothing can be changed by hand until a case moves to Needs review."),
        Queue.Expiring => new("expiring", "Authorizations ending soon", "Capture these before the window ends, or the funds are released."),
        _ => throw new ArgumentOutOfRangeException(nameof(queue)),
    };

    /// <summary>Why this person can't act from a queue, or null when they can or nobody can.</summary>
    public static string? RoleNote(Queue queue, ConsoleRole role) => queue switch
    {
        Queue.Review when !ConsolePolicies.Allows(role, ConsolePolicies.CanAct) => "Proposing needs the Operator role.",
        Queue.Approval when !ConsolePolicies.Allows(role, ConsolePolicies.CanApprove) => "Deciding needs the Approver role.",
        Queue.Expiring when !ConsolePolicies.Allows(role, ConsolePolicies.CanAct) => "Capturing or voiding needs the Operator role.",
        _ => null,
    };

    public const string OwnProposalNote = "You proposed this, so another Approver decides.";

    public const string StuckVoidNote = "This console can't settle a stuck void. Handle it at the PSP.";

    public static string Reason(AttentionRow row, DateTimeOffset now)
    {
        var age = Duration(now - row.At);
        var operation = row.Subject.StartsWith("attempt:", StringComparison.Ordinal) ? row.Subject["attempt:".Length..] : null;
        return row.Queue switch
        {
            Queue.Review => row.Subject switch
            {
                "payment" => "The PSP's answer disagrees with our record",
                "refund" => $"A refund of {MoneyFormat.DisplayWithCode(row.AmountMinor, row.Currency)} needs review",
                _ => $"A {operation} attempt needs review",
            } + (row.Proposed ? ". A resolution is waiting for approval." : string.Empty),
            Queue.Approval => $"{Outcome(row.Detail)}, proposed by {row.ActorEmail ?? "a staff member"}",
            Queue.Orphan => $"A {row.Subject["action:".Length..]} by {row.ActorEmail ?? "a staff member"} has no recorded result. Started {age} ago.",
            Queue.Unresolved => row.Subject switch
            {
                "payment" when row.Detail == "UNKNOWN" => $"Outcome unknown for {age}",
                "payment" => $"Sent to the PSP, no answer for {age}",
                "refund" => $"A refund of {MoneyFormat.DisplayWithCode(row.AmountMinor, row.Currency)}: no answer for {age}",
                _ => $"A {operation}: no answer for {age}",
            },
            Queue.Expiring => row.At > now ? "Ends in " + Duration(row.At - now) : "The authorization window has ended",
            _ => string.Empty,
        };
    }

    /// <summary>Whether a row has waited more than twice the queue's limit.</summary>
    public static bool OverLimit(AttentionRow row, DateTimeOffset now, AttentionOptions options) =>
        row.Queue == Queue.Unresolved && now - row.At > options.UnresolvedAfter * 2;

    public static bool IsStuckVoid(AttentionRow row) => row.Queue == Queue.Unresolved && row.Subject == "attempt:void";

    /// <summary>"40 min", "3 h 12 min", "5 h", "3 d".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "less than a minute";
        }

        var minutes = (int)span.TotalMinutes;
        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        var hours = minutes / 60;
        if (hours >= 48)
        {
            return (hours / 24).ToString(CultureInfo.InvariantCulture) + " d";
        }

        var rest = minutes % 60;
        return hours.ToString(CultureInfo.InvariantCulture) + " h" + (rest == 0 ? string.Empty : " " + rest.ToString(CultureInfo.InvariantCulture) + " min");
    }

    /// <summary>"1,000+" once a count reaches its cap.</summary>
    public static string Count(int count) =>
        count > AttentionOptions.CountCap
            ? AttentionOptions.CountCap.ToString("N0", CultureInfo.InvariantCulture) + "+"
            : count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Badge(int count) => count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);

    private static string Outcome(string? outcome) => outcome switch
    {
        "authorize" => "Mark as authorized",
        "awaiting_method" => "Return to waiting for payment",
        "capture" => "Mark as captured",
        "confirm" => "Confirm the refund",
        "release" => "Release the refund",
        "reversed_at_psp" => "Record a reversal made at the PSP",
        _ => outcome ?? "A resolution",
    };
}
