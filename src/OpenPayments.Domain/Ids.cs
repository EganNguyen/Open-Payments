namespace OpenPayments.Domain;

/// <summary>A UUIDv7-backed payment identifier.</summary>
public readonly record struct PaymentId(Guid Value)
{
    public static PaymentId New() => new(Guid.CreateVersion7());

    public static PaymentId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out PaymentId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new PaymentId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>A UUIDv7-backed attempt identifier. It is also the PSP idempotency key.</summary>
public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.CreateVersion7());

    public static AttemptId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out AttemptId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new AttemptId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>A UUIDv7-backed refund identifier. It is also the PSP idempotency key.</summary>
public readonly record struct RefundId(Guid Value)
{
    public static RefundId New() => new(Guid.CreateVersion7());

    public static RefundId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out RefundId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new RefundId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>A UUIDv7-backed merchant identifier.</summary>
public readonly record struct MerchantId(Guid Value)
{
    public static MerchantId New() => new(Guid.CreateVersion7());

    public static MerchantId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out MerchantId id)
    {
        if (Guid.TryParse(value, out var guid))
        {
            id = new MerchantId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}
