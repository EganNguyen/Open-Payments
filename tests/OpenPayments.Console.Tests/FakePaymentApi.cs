using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace OpenPayments.Console.Tests;

/// <summary>Stands in for the payment API: records every call by idempotency key and answers
/// with whatever the test queues, or 200 with the given state.</summary>
public sealed class FakePaymentApi : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();

    public ConcurrentQueue<(string Path, string Key, string Body)> Calls { get; } = new();

    public string DoneState { get; set; } = "CAPTURED";

    public void Then(HttpStatusCode status, string json = "{}", TimeSpan? retryAfter = null) =>
        _answers.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (retryAfter is { } after)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(after);
            }

            return response;
        });

    public void ThenTimeout() => _answers.Enqueue(_ => throw new TaskCanceledException("timeout"));

    public Task<HttpResponseMessage> SendForTestAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

    public int CallsFor(string key) => Calls.Count(call => call.Key == key);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue((
            request.RequestUri!.AbsolutePath,
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : string.Empty,
            body));
        if (_answers.TryDequeue(out var answer))
        {
            return answer(request);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"state":"{{DoneState}}"}""", Encoding.UTF8, "application/json"),
        };
    }
}
