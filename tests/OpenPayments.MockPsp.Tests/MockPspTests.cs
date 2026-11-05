using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenPayments.MockPsp.Tests;

public sealed class MockPspTests
{
    [Fact]
    public async Task Same_key_returns_stored_answer_and_one_effect()
    {
        await using var app = await MockPspApp.StartAsync();

        var first = await app.PostAsync("/authorize", Amount(100));
        var second = await app.PostAsync("/authorize", Amount(100));

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("approved", first.String("outcome"));
        Assert.Equal(first.String("psp_reference"), second.String("psp_reference"));
        Assert.Equal(first.Long("amount_minor"), second.Long("amount_minor"));
        Assert.Equal(1, app.EffectCount());
    }

    [Fact]
    public async Task Hundred_concurrent_same_key_one_effect()
    {
        await using var app = await MockPspApp.StartAsync();

        var calls = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ =>
                app.PostAsync("/authorize", Amount(250), idempotencyKey: "concurrent-key")));

        Assert.All(calls, call => Assert.Equal(HttpStatusCode.OK, call.Status));
        Assert.All(calls, call => Assert.Equal("approved", call.String("outcome")));
        Assert.Single(calls.Select(call => call.String("psp_reference")).Distinct());
        Assert.Equal(1, app.EffectCount());
    }

    [Fact]
    public async Task Approve_then_hang_records_effect_then_never_answers()
    {
        await using var app = await MockPspApp.StartAsync(
            new { approve_then_hang_rate = 1.0 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => app.PostWithTimeoutAsync(
                "/authorize",
                Amount(300),
                TimeSpan.FromMilliseconds(300),
                "hang-key"));

        Assert.Equal(1, app.EffectCount());
        var inquiry = await app.InquireAsync("hang-key");
        Assert.Equal("approved", inquiry.String("status"));
    }

    [Fact]
    public async Task Inquire_unseen_key_returns_unknown_attempt()
    {
        await using var app = await MockPspApp.StartAsync();

        var inquiry = await app.InquireAsync("never-seen");

        Assert.Equal(HttpStatusCode.OK, inquiry.Status);
        Assert.Equal("unknown_attempt", inquiry.String("status"));
    }

    [Fact]
    public async Task Evicted_key_returns_evicted_never_unknown_attempt()
    {
        await using var app = await MockPspApp.StartAsync(new { key_retention_s = 0.0 });
        await app.PostAsync("/authorize", Amount(400), idempotencyKey: "evicted-key");

        var inquiry = await app.InquireAsync("evicted-key");

        Assert.Equal("evicted", inquiry.String("status"));
        Assert.NotEqual("unknown_attempt", inquiry.String("status"));
    }

    [Fact]
    public async Task Same_seed_gives_same_outcomes()
    {
        await using var one = await MockPspApp.StartAsync(
            new { seed = 1234, decline_rate = 0.5, action_rate = 0.2 });
        await using var two = await MockPspApp.StartAsync(
            new { seed = 1234, decline_rate = 0.5, action_rate = 0.2 });

        for (var index = 0; index < 20; index++)
        {
            var key = $"seed-key-{index}";
            var first = await one.PostAsync("/authorize", Amount(500), idempotencyKey: key);
            var second = await two.PostAsync("/authorize", Amount(500), idempotencyKey: key);
            Assert.Equal(first.String("outcome"), second.String("outcome"));
        }
    }

    [Fact]
    public async Task Over_rate_limit_returns_429_with_no_effect()
    {
        await using var app = await MockPspApp.StartAsync(new { rate_limit_per_s = 1 });
        var first = await app.PostAsync("/authorize", Amount(600), idempotencyKey: "rate-1");
        var second = await app.PostAsync("/authorize", Amount(600), idempotencyKey: "rate-2");

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
        Assert.Equal(1, app.EffectCount());
        Assert.Equal("1", second.RetryAfter());
    }

    [Fact]
    public async Task Sale_records_one_effect_of_kind_sale()
    {
        await using var app = await MockPspApp.StartAsync();

        var sale = await app.PostAsync("/sale", Amount(700), idempotencyKey: "sale-key");

        Assert.Equal(HttpStatusCode.OK, sale.Status);
        var line = Assert.Single(app.EffectDataLines());
        var columns = line.Split(',');
        Assert.Equal("sale-key", columns[0]);
        Assert.Equal("sale", columns[1]);
        Assert.Equal("700", columns[2]);
        Assert.Equal("USD", columns[3]);
    }

    [Fact]
    public async Task Late_landing_lookup_says_not_found_then_effect_appears()
    {
        await using var app = await MockPspApp.StartAsync(
            new { late_landing_rate = 1.0, lookup_lag_s = 0.5 });

        var call = await app.PostAsync("/authorize", Amount(800), idempotencyKey: "late-key");
        Assert.Equal("approved", call.String("outcome"));

        var immediate = await app.InquireAsync("late-key");
        Assert.Equal("unknown_attempt", immediate.String("status"));
        Assert.Equal(0, app.EffectCount());

        await TestWait.UntilAsync(() => app.EffectCount() == 1);
        var later = await app.InquireAsync("late-key");
        Assert.Equal("approved", later.String("status"));
    }

    [Fact]
    public async Task Lookup_lag_hides_a_real_effect_until_the_lag_passes()
    {
        await using var app = await MockPspApp.StartAsync(
            new { lookup_lag_s = 0.5 });

        await app.PostAsync("/authorize", Amount(900), idempotencyKey: "lag-key");
        Assert.Equal(1, app.EffectCount());

        var immediate = await app.InquireAsync("lag-key");
        Assert.Equal("unknown_attempt", immediate.String("status"));

        await TestWait.UntilAsync(
            async () => (await app.InquireAsync("lag-key")).String("status") == "approved");
    }

    [Fact]
    public async Task Replay_mode_runs_a_never_received_request_once()
    {
        await using var app = await MockPspApp.StartAsync(
            new { recovery_mode = "replay", error_rate = 1.0 });

        var lost = await app.PostAsync("/authorize", Amount(1000), idempotencyKey: "replay-key");
        Assert.Equal(HttpStatusCode.InternalServerError, lost.Status);
        Assert.Equal(0, app.EffectCount());

        await app.ConfigureAsync(new { error_rate = 0.0 });
        var replay = await app.PostAsync("/authorize", Amount(1000), idempotencyKey: "replay-key");

        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal("approved", replay.String("outcome"));
        Assert.Equal(1, app.EffectCount());
    }

    [Fact]
    public async Task Replay_after_retention_creates_a_second_effect()
    {
        await using var app = await MockPspApp.StartAsync(new { key_retention_s = 0.0 });

        await app.PostAsync("/authorize", Amount(1100), idempotencyKey: "replayed-key");
        await app.PostAsync("/authorize", Amount(1100), idempotencyKey: "replayed-key");

        Assert.Equal(2, app.EffectCount());
    }

    [Fact]
    public async Task Cancel_by_reference_blocks_a_late_landing_and_calls_back()
    {
        await using var callbacks = new CallbackListener(failFirst: false);
        await using var app = await MockPspApp.StartAsync(
            new
            {
                late_landing_rate = 1.0,
                lookup_lag_s = 0.5,
                callback_url = callbacks.Url,
                callback_rate = 0.0,
            });

        await app.PostAsync("/authorize", Amount(1200), idempotencyKey: "cancel-key");
        var cancel = await app.PostAsync(
            "/cancel-by-reference/cancel-key",
            new { },
            idempotencyKey: null);

        Assert.Equal(HttpStatusCode.OK, cancel.Status);
        await TestWait.UntilAsync(() => callbacks.Count == 1);
        Assert.Contains("cancel-key", callbacks.Bodies.Single(), StringComparison.Ordinal);

        await Task.Delay(700);
        Assert.Equal(0, app.EffectCount());
        var inquiry = await app.InquireAsync("cancel-key");
        Assert.Equal("declined", inquiry.String("status"));
        Assert.Equal("cancelled", inquiry.String("failure_code"));
    }

    [Fact]
    public async Task Callbacks_are_sent_at_the_configured_rate_and_redelivered_on_failure()
    {
        await using var callbacks = new CallbackListener(failFirst: true);
        await using var app = await MockPspApp.StartAsync(
            new
            {
                callback_rate = 1.0,
                callback_url = callbacks.Url,
                callback_delay_ms = 0,
            });

        await app.PostAsync("/authorize", Amount(1300), idempotencyKey: "callback-key");

        await TestWait.UntilAsync(() => callbacks.Count == 2);
        Assert.Equal(2, callbacks.Count);
        Assert.All(callbacks.Bodies, body => Assert.Contains("callback-key", body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Void_after_capture_is_refused_and_records_no_effect()
    {
        await using var app = await MockPspApp.StartAsync();
        var sale = await app.PostAsync("/sale", Amount(1400), idempotencyKey: "void-sale");
        var reference = sale.String("psp_reference");
        Assert.NotNull(reference);

        var voidCall = await app.PostAsync(
            "/void",
            new { psp_reference = reference },
            idempotencyKey: "void-key");

        Assert.Equal(HttpStatusCode.Conflict, voidCall.Status);
        Assert.Equal(1, app.EffectCount());
    }

    [Fact]
    public async Task Rate_limit_applies_per_account()
    {
        await using var app = await MockPspApp.StartAsync(new { rate_limit_per_s = 1 });

        var aFirst = await app.PostAsync("/authorize", Amount(1500), account: "account-a", idempotencyKey: "a-1");
        var aSecond = await app.PostAsync("/authorize", Amount(1500), account: "account-a", idempotencyKey: "a-2");
        var bFirst = await app.PostAsync("/authorize", Amount(1500), account: "account-b", idempotencyKey: "b-1");

        Assert.Equal(HttpStatusCode.OK, aFirst.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, aSecond.Status);
        Assert.Equal(HttpStatusCode.OK, bFirst.Status);
        Assert.Equal(2, app.EffectCount());
    }

    [Fact]
    public async Task Revoked_account_returns_auth_failure_with_no_effect()
    {
        await using var app = await MockPspApp.StartAsync();
        await app.ConfigureAccountAsync("revoked-account", new { revoked = true });

        var call = await app.PostAsync(
            "/authorize",
            Amount(1600),
            account: "revoked-account",
            idempotencyKey: "revoked-key");

        Assert.Equal(HttpStatusCode.Unauthorized, call.Status);
        Assert.Equal(0, app.EffectCount());
    }

    private static object Amount(long amountMinor) =>
        new { amount_minor = amountMinor, currency = "USD" };

    private sealed record MockCall(HttpStatusCode Status, string Raw, JsonNode? Body)
    {
        public string? String(string name) => Body?[name]?.GetValue<string>();

        public long? Long(string name) => Body?[name]?.GetValue<long>();

        public string? RetryAfter() => Body?["retry_after"]?.GetValue<string>();
    }

    private sealed class MockPspApp : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        private MockPspApp(WebApplicationFactory<Program> factory, HttpClient client, string effectsDir)
        {
            _factory = factory;
            Client = client;
            EffectsDir = effectsDir;
        }

        public HttpClient Client { get; }

        public string EffectsDir { get; }

        public static async Task<MockPspApp> StartAsync(object? config = null)
        {
            var effectsDir = Path.Combine(
                Path.GetTempPath(),
                "op-mock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(effectsDir);
            var factory = new MockPspFactory { EffectsDir = effectsDir };
            var app = new MockPspApp(factory, factory.CreateClient(), effectsDir);
            if (config is not null)
            {
                await app.ConfigureAsync(config);
            }

            return app;
        }

        public async Task ConfigureAsync(object values)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/config")
            {
                Content = JsonContent(values),
            };
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task ConfigureAccountAsync(string account, object values)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                "/admin/accounts/" + Uri.EscapeDataString(account))
            {
                Content = JsonContent(values),
            };
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<MockCall> PostAsync(
            string path,
            object body,
            string account = "account-a",
            string? idempotencyKey = "key-1")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent(body),
            };
            request.Headers.Add("X-Mock-Account", account);
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            using var response = await Client.SendAsync(request);
            return await ToCallAsync(response);
        }

        public async Task<MockCall> PostWithTimeoutAsync(
            string path,
            object body,
            TimeSpan timeout,
            string? idempotencyKey)
        {
            using var client = _factory.CreateClient();
            client.Timeout = timeout;
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent(body),
            };
            request.Headers.Add("X-Mock-Account", "account-a");
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            using var response = await client.SendAsync(request);
            return await ToCallAsync(response);
        }

        public async Task<MockCall> InquireAsync(string key, string account = "account-a")
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/inquire/" + Uri.EscapeDataString(key));
            request.Headers.Add("X-Mock-Account", account);
            using var response = await Client.SendAsync(request);
            return await ToCallAsync(response);
        }

        public IReadOnlyList<string> EffectDataLines()
        {
            var path = Path.Combine(EffectsDir, "effects.csv");
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToList();
            return lines.Count > 0 && lines[0].StartsWith("key,op,", StringComparison.Ordinal)
                ? lines.Skip(1).ToList()
                : lines;
        }

        public int EffectCount() => EffectDataLines().Count;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            try
            {
                Directory.Delete(EffectsDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static StringContent JsonContent(object values) =>
            new(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json");

        private static async Task<MockCall> ToCallAsync(HttpResponseMessage response)
        {
            var raw = await response.Content.ReadAsStringAsync();
            var body = raw.Length == 0 ? null : JsonNode.Parse(raw);
            return new MockCall(response.StatusCode, raw, body);
        }
    }

    private sealed class MockPspFactory : WebApplicationFactory<Program>
    {
        public required string EffectsDir { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("EFFECTS_DIR", EffectsDir);
        }
    }

    private sealed class CallbackListener : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly bool _failFirst;
        private int _count;

        public CallbackListener(bool failFirst)
        {
            _failFirst = failFirst;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/callbacks/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public int Count => Volatile.Read(ref _count);

        public ConcurrentQueue<string> Bodies { get; } = new();

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loop;
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                Bodies.Enqueue(body);
                var count = Interlocked.Increment(ref _count);
                context.Response.StatusCode = _failFirst && count == 1 ? 500 : 200;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private static class TestWait
    {
        public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Condition was not met before the timeout.");
                }

                await Task.Delay(25);
            }
        }

        public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!await condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Condition was not met before the timeout.");
                }

                await Task.Delay(25);
            }
        }
    }
}
