using OpenPayments.Domain;

namespace OpenPayments.Psp;

/// <summary>Verifies and parses one PSP callback (D30, D59). It writes nothing.</summary>
public interface IPspEventParser
{
    Result<PspEvent> Parse(
        ReadOnlySpan<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        string signingSecret,
        DateTimeOffset now);
}
