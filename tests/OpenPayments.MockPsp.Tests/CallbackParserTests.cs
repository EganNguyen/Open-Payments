using System.Security.Cryptography;
using System.Text;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.MockPsp.Tests;

// Task 25: the callback parser verifies the raw body before anything is written.
public sealed class CallbackParserTests
{
    private const string Secret = "whsec_parser_test";

    [Fact]
    public void Signed_event_verifies_and_parses()
    {
        var reference = Guid.NewGuid();
        var body = Body("authorize.approved", reference);
        var now = DateTimeOffset.UtcNow;
        var headers = Sign(body, now, Secret);

        var result = new MockPspEventParser().Parse(
            Encoding.UTF8.GetBytes(body), headers, Secret, now);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(PspEventKind.Authorized, result.Value.Kind);
        Assert.Equal(reference, result.Value.Reference.Attempt!.Value.Value);
        Assert.Equal(1000, result.Value.Amount!.Value.Minor);
    }

    [Fact]
    public void Refund_event_names_a_refund()
    {
        var reference = Guid.NewGuid();
        var body = Body("refund.approved", reference);
        var now = DateTimeOffset.UtcNow;

        var result = new MockPspEventParser().Parse(
            Encoding.UTF8.GetBytes(body), Sign(body, now, Secret), Secret, now);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(reference, result.Value.Reference.Refund!.Value.Value);
    }

    [Fact]
    public void Tampered_body_fails_the_signature()
    {
        var body = Body("authorize.approved", Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var headers = Sign(body, now, Secret);
        var tampered = body.Replace("1000", "2000", StringComparison.Ordinal);

        var result = new MockPspEventParser().Parse(
            Encoding.UTF8.GetBytes(tampered), headers, Secret, now);

        Assert.True(result.IsFailure);
        Assert.Equal("bad_signature", result.ErrorCode);
    }

    [Fact]
    public void Stale_timestamp_fails()
    {
        var body = Body("authorize.approved", Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var result = new MockPspEventParser().Parse(
            Encoding.UTF8.GetBytes(body), Sign(body, now.AddMinutes(-10), Secret), Secret, now);

        Assert.True(result.IsFailure);
        Assert.Equal("stale_timestamp", result.ErrorCode);
    }

    [Fact]
    public void Unknown_type_fails()
    {
        var body = Body("payment.weird", Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var result = new MockPspEventParser().Parse(
            Encoding.UTF8.GetBytes(body), Sign(body, now, Secret), Secret, now);

        Assert.True(result.IsFailure);
        Assert.Equal("unknown_event_type", result.ErrorCode);
    }

    private static string Body(string type, Guid reference) =>
        $$"""{"event_id":"evt_1","type":"{{type}}","reference":"{{reference}}","psp_reference":"psp_1","amount_minor":1000,"currency":"USD"}""";

    private static Dictionary<string, string> Sign(string body, DateTimeOffset at, string secret)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signed = timestamp + "." + body;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mock-Signature"] = $"t={timestamp},v1={signature}",
        };
    }
}
