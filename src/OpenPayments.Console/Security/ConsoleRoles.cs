namespace OpenPayments.Console.Security;

public enum ConsoleRole
{
    Viewer,
    Operator,
    Approver,
    Admin,
}

/// <summary>The spec's role table, and the one place policies are decided.</summary>
public static class ConsolePolicies
{
    public const string CanRead = nameof(CanRead);
    public const string CanAct = nameof(CanAct);
    public const string CanApprove = nameof(CanApprove);
    public const string CanAdmin = nameof(CanAdmin);

    public static readonly IReadOnlyList<string> All = [CanRead, CanAct, CanApprove, CanAdmin];

    // Viewer reads. Operator also acts. Approver also approves someone else's proposal.
    // Admin manages staff and has no money action.
    public static bool Allows(ConsoleRole role, string policy) => policy switch
    {
        CanRead => true,
        CanAct => role is ConsoleRole.Operator or ConsoleRole.Approver,
        CanApprove => role is ConsoleRole.Approver,
        CanAdmin => role is ConsoleRole.Admin,
        _ => false,
    };

    public static bool TryParse(string value, out ConsoleRole role)
    {
        switch (value)
        {
            case "viewer": role = ConsoleRole.Viewer; return true;
            case "operator": role = ConsoleRole.Operator; return true;
            case "approver": role = ConsoleRole.Approver; return true;
            case "admin": role = ConsoleRole.Admin; return true;
            default: role = default; return false;
        }
    }

    public static string ToText(ConsoleRole role) => role switch
    {
        ConsoleRole.Viewer => "viewer",
        ConsoleRole.Operator => "operator",
        ConsoleRole.Approver => "approver",
        ConsoleRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
