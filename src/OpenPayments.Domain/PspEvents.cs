namespace OpenPayments.Domain;

public enum PspEventKind
{
    Authorized,
    Captured,
    SaleCaptured,
    Declined,
    ActionRequired,
    Voided,
    VoidFailed,
    Refunded,
    RefundFailed,
    Canceled,
}

/// <summary>Which of our operations a PSP event names: an attempt or a refund.</summary>
public sealed record PspEventReference(AttemptId? Attempt, RefundId? Refund)
{
    public static PspEventReference ForAttempt(AttemptId id) => new(id, null);

    public static PspEventReference ForRefund(RefundId id) => new(null, id);
}

public sealed record PspEvent(
    string EventId,
    PspEventKind Kind,
    PspEventReference Reference,
    string? PspReference,
    Money? Amount,
    string? FailureCode,
    string? ActionType);

/// <summary>Parses a stored callback payload without its signature (the row is already trusted).</summary>
public static class PspEventJson
{
    public static Result<PspEvent> Parse(string payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
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

            PspEventReference ids;
            if (kind is PspEventKind.Refunded or PspEventKind.RefundFailed)
            {
                if (!Guid.TryParse(reference, out var refund))
                {
                    return Result<PspEvent>.Fail("invalid_reference");
                }

                ids = PspEventReference.ForRefund(new RefundId(refund));
            }
            else
            {
                if (!Guid.TryParse(reference, out var attempt))
                {
                    return Result<PspEvent>.Fail("invalid_reference");
                }

                ids = PspEventReference.ForAttempt(new AttemptId(attempt));
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
                eventId, kind, ids,
                root.TryGetProperty("psp_reference", out var psp) ? psp.GetString() : null,
                amount,
                root.TryGetProperty("failure_code", out var failure) ? failure.GetString() : null,
                root.TryGetProperty("action_type", out var action) ? action.GetString() : null));
        }
        catch (System.Text.Json.JsonException)
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
