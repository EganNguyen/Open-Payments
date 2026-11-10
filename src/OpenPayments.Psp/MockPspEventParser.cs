using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenPayments.Domain;

namespace OpenPayments.Psp;

/// <summary>
/// The mock PSP's signed callback: header <c>Mock-Signature: t=…,v1=hex(HMAC-SHA256(secret, "t.body"))</c>,
/// body fields <c>event_id, type, reference, psp_reference, amount_minor, currency</c>.
/// </summary>
public sealed class MockPspEventParser : IPspEventParser
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public Result<PspEvent> Parse(
        ReadOnlySpan<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        string signingSecret,
        DateTimeOffset now)
    {
        var header = headers
            .FirstOrDefault(h => string.Equals(h.Key, "Mock-Signature", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(header))
        {
            return Result<PspEvent>.Fail("missing_signature");
        }

        var parts = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timestampText = parts.FirstOrDefault(p => p.StartsWith("t=", StringComparison.Ordinal))?[2..];
        var signature = parts.FirstOrDefault(p => p.StartsWith("v1=", StringComparison.Ordinal))?[3..];
        if (timestampText is null
            || signature is null
            || !long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            return Result<PspEvent>.Fail("bad_signature");
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((now - timestamp).Duration() > Tolerance)
        {
            return Result<PspEvent>.Fail("stale_timestamp");
        }

        var signed = Encoding.UTF8.GetBytes(timestampText + "." + Encoding.UTF8.GetString(rawBody));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var expected = hmac.ComputeHash(signed);
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return Result<PspEvent>.Fail("bad_signature");
        }

        if (actual.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return Result<PspEvent>.Fail("bad_signature");
        }

        return PspEventJson.Parse(Encoding.UTF8.GetString(rawBody));
    }

}
