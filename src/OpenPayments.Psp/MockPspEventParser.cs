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

        try
        {
            using var document = JsonDocument.Parse(rawBody.ToArray());
            var root = document.RootElement;
            var eventId = root.GetProperty("event_id").GetString();
            var type = root.GetProperty("type").GetString();
            var reference = root.GetProperty("reference").GetString();
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(reference))
            {
                return Result<PspEvent>.Fail("invalid_event");
            }

            if (!TryKind(type, out var kind))
            {
                return Result<PspEvent>.Fail("unknown_event_type");
            }

            PspEventReference referenceIds;
            if (kind is PspEventKind.Refunded or PspEventKind.RefundFailed)
            {
                if (!Guid.TryParse(reference, out var refund))
                {
                    return Result<PspEvent>.Fail("invalid_reference");
                }

                referenceIds = PspEventReference.ForRefund(new RefundId(refund));
            }
            else
            {
                if (!Guid.TryParse(reference, out var attempt))
                {
                    return Result<PspEvent>.Fail("invalid_reference");
                }

                referenceIds = PspEventReference.ForAttempt(new AttemptId(attempt));
            }

            Money? amount = null;
            if (root.TryGetProperty("amount_minor", out var amountElement)
                && root.TryGetProperty("currency", out var currencyElement)
                && amountElement.TryGetInt64(out var minor)
                && CurrencyCode.TryParse(currencyElement.GetString()?.ToUpperInvariant(), out var currency))
            {
                amount = new Money(minor, currency);
            }

            return Result<PspEvent>.Ok(new PspEvent(
                eventId,
                kind,
                referenceIds,
                root.TryGetProperty("psp_reference", out var psp) ? psp.GetString() : null,
                amount,
                root.TryGetProperty("failure_code", out var failure) ? failure.GetString() : null,
                root.TryGetProperty("action_type", out var action) ? action.GetString() : null));
        }
        catch (JsonException)
        {
            return Result<PspEvent>.Fail("invalid_event");
        }
        catch (KeyNotFoundException)
        {
            return Result<PspEvent>.Fail("invalid_event");
        }
    }

    private static bool TryKind(string type, out PspEventKind kind)
    {
        kind = type switch
        {
            "authorize.approved" or "authorize.resolved" => PspEventKind.Authorized,
            "sale.approved" or "sale.resolved" => PspEventKind.SaleCaptured,
            "capture.approved" or "capture.resolved" => PspEventKind.Captured,
            "authorize.declined" or "capture.declined" or "sale.declined" => PspEventKind.Declined,
            "authorize.action_required" => PspEventKind.ActionRequired,
            "void.approved" => PspEventKind.Voided,
            "void.declined" => PspEventKind.VoidFailed,
            "refund.approved" or "refund.resolved" => PspEventKind.Refunded,
            "refund.declined" => PspEventKind.RefundFailed,
            "authorize.canceled" or "capture.canceled" or "sale.canceled" => PspEventKind.Canceled,
            _ => (PspEventKind)(-1),
        };
        return (int)kind >= 0;
    }
}
