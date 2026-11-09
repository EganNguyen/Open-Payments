using System.Net;
using System.Text;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.MockPsp.Tests;

// Review finding H2, D27: anything other than an explicit answer is inconclusive. The adapter must
// turn a transport failure or an unreadable body into PspUnavailableException, which every route
// records as UNKNOWN and answers 202, never a 500.
public sealed class AdapterTransportTests
{
    private static readonly PspAccount Account = new(Guid.NewGuid(), new MerchantId(Guid.NewGuid()), "mock", "live", "ref");

    public static TheoryData<string> Failures() => ["reset", "malformed", "truncated"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Transport_failure_after_sending_is_unavailable_not_an_error(string failure)
    {
        using var handler = new Failing(failure);
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://psp.test/") };
        var adapter = new MockPspAdapter(client, Account);

        await Assert.ThrowsAsync<PspUnavailableException>(() => adapter.AuthorizeAsync(
            new AttemptId(Guid.NewGuid()),
            new Money(1000, CurrencyCode.Parse("USD")),
            new PaymentMethodRef("card", "tok_test"),
            CancellationToken.None));
    }

    private sealed class Failing(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure == "reset")
            {
                throw new HttpRequestException("Connection reset by peer", new IOException("reset"));
            }

            var body = failure == "malformed" ? "{\"outcome\": approved" : "{\"outcome\":\"appro";
#pragma warning disable CA2000 // The HttpClient owns and disposes the response.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
#pragma warning restore CA2000
            return Task.FromResult(response);
        }
    }
}
