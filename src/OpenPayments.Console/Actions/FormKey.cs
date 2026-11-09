using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OpenPayments.Console.Actions;

/// <summary>
/// One idempotency key per form, issued when the form renders. The key travels inside a token
/// protected by ASP.NET Data Protection and bound to the payment, the action and the person, so
/// a token can't be replayed against another payment, action or staff member, and a double tap
/// or a resubmit sends the same key.
/// </summary>
public sealed class FormKey(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("OpenPayments.Console.FormKey.v1");

    public string Issue(Guid paymentId, string action, Guid staffId)
    {
        var key = "console-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return _protector.Protect(string.Join('|', paymentId.ToString("N"), action, staffId.ToString("N"), key));
    }

    public bool TryRead(string? token, Guid paymentId, string action, Guid staffId, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4
            || parts[0] != paymentId.ToString("N")
            || parts[1] != action
            || parts[2] != staffId.ToString("N"))
        {
            return false;
        }

        key = parts[3];
        return true;
    }
}
