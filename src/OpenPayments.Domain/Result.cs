namespace OpenPayments.Domain;

/// <summary>
/// The outcome of a validation or parsing operation. A failure carries a stable code,
/// never a formatted message, so callers can map it to the API contract.
/// </summary>
public readonly struct Result<T>
{
    private readonly T _value;

    private Result(bool isSuccess, T value, string? errorCode)
    {
        IsSuccess = isSuccess;
        _value = value;
        ErrorCode = errorCode;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? ErrorCode { get; }

    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("A failed result has no value.");

    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(string errorCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return new Result<T>(false, default!, errorCode);
    }
}
