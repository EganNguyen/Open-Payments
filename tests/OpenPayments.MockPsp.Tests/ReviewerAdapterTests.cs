using System.Net;
using System.Text;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.MockPsp.Tests;

// Stage 1 review, third re-check: written by the reviewer, not the builder.
// D27: once the request was sent, any answer the adapter can't turn into a result is
// inconclusive. The routes treat only PspUnavailableException, PspTimeoutException and
// OperationCanceledException that way; anything else escapes as a 500 after the PSP acted.
public sealed class ReviewerAdapterTests
{
    private static readonly PspAccount Account = new(Guid.NewGuid(), new MerchantId(Guid.NewGuid()), "mock", "live", "ref");

    [Theory]
    [InlineData("{\"outcome\":\"approved\",\"psp_reference\":\"p1\",\"amount_minor\":1000,\"currency\":\"usd\"}")]
    [InlineData("{\"outcome\":\"approved\",\"psp_reference\":\"p1\",\"amount_minor\":1000,\"currency\":\"EUR\"}")]
    [InlineData("{\"outcome\":\"action_required\"}")]
    public async Task R10_an_approved_answer_the_adapter_cant_read_is_inconclusive(string body)
    {
        using var handler = new Answering(body);
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://psp.test/") };
        var adapter = new MockPspAdapter(client, Account);

        Exception? thrown = null;
        try
        {
            await adapter.SaleAsync(
                new AttemptId(Guid.NewGuid()),
                new Money(1000, CurrencyCode.Parse("USD")),
                new PaymentMethodRef("card", "tok_test"),
                CancellationToken.None);
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            thrown = exception;
        }

        Assert.True(
            thrown is null or PspUnavailableException or PspTimeoutException or OperationCanceledException,
            "escaped as " + thrown?.GetType().Name + ": " + thrown?.Message);
    }

    private sealed class Answering(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
#pragma warning disable CA2000
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
#pragma warning restore CA2000
        }
    }
}
