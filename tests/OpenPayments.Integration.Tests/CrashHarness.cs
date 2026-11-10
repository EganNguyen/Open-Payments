using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace OpenPayments.Integration.Tests;

/// <summary>
/// The API and the mock PSP as real processes, so a test can kill the API at a chosen moment
/// and start it again, as a crash and a restart would. The database is the C43 fixture's
/// primary, through PgBouncer, with a standby the test can pause to hold a COMMIT in SyncRep.
/// </summary>
public sealed class CrashHarness : IAsyncDisposable
{
    public const string ApiKey = "crash_test_key";

    private readonly C43Fixture _db;
    private readonly string _effectsDirectory = Path.Combine(Path.GetTempPath(), "op-crash-" + Guid.NewGuid().ToString("N"));
    private Process? _api;
    private Process? _mock;

    private CrashHarness(C43Fixture db, Guid pspAccount)
    {
        _db = db;
        PspAccount = pspAccount;
        ApiPort = FreePort();
        MockPort = FreePort();
        Api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}"), Timeout = TimeSpan.FromSeconds(30) };
        Api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        Mock = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{MockPort}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public Guid PspAccount { get; }

    public int ApiPort { get; }

    public int MockPort { get; }

    public HttpClient Api { get; }

    public HttpClient Mock { get; }

    public static async Task<CrashHarness> StartAsync(C43Fixture db, object mockConfig)
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        await using (var connection = await db.OpenAdminAsync())
        await using (var command = new NpgsqlCommand(
            "insert into api_keys (key_hash, merchant_id, mode, scopes) values (@hash, @merchant, 'live', array['payments:write','payments:read'])",
            connection))
        {
            command.Parameters.AddWithValue("hash", SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey)));
            command.Parameters.AddWithValue("merchant", seed.Merchant.Value);
            await command.ExecuteNonQueryAsync();
        }

        var harness = new CrashHarness(db, seed.PspAccountId);
        Directory.CreateDirectory(harness._effectsDirectory);
        harness._mock = Start("tools/OpenPayments.MockPsp/OpenPayments.MockPsp", harness.MockPort, new()
        {
            ["EFFECTS_DIR"] = harness._effectsDirectory,
        });
        await WaitHealthyAsync(harness.Mock);
        using (var configured = await harness.Mock.PutAsJsonAsync("/admin/config", mockConfig, SnakeCase))
        {
            configured.EnsureSuccessStatusCode();
        }

        await harness.StartApiAsync();
        return harness;
    }

    public async Task StartApiAsync()
    {
        _api = Start("src/OpenPayments.Api/OpenPayments.Api", ApiPort, new()
        {
            ["DATABASE_URL"] = _db.ConnectionString("pgbouncer", maxPoolSize: 20),
            ["PSP_URL"] = $"http://127.0.0.1:{MockPort}",
        });
        await WaitHealthyAsync(Api);
    }

    /// <summary>Kill the API at once, with no shutdown: a crash.</summary>
    public void CrashApi()
    {
        _api!.Kill(entireProcessTree: true);
        _api.WaitForExit(10_000);
        _api.Dispose();
        _api = null;
    }

    public async Task<long> MockStatAsync(string name)
    {
        var stats = await Mock.GetFromJsonAsync<Dictionary<string, long>>("/admin/stats");
        return stats![name];
    }

    public async Task WaitForMockStatAsync(string name, long atLeast)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await MockStatAsync(name) >= atLeast)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"The mock's '{name}' never reached {atLeast}.");
    }

    /// <summary>Ask the mock what happened to a key, as Stage 2's recovery will (D27).</summary>
    public async Task<string?> InquireAsync(Guid key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/inquire/" + key.ToString("D"));
        request.Headers.Add("X-Mock-Account", PspAccount.ToString("D"));
        using var response = await Mock.SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("status", out var status) ? status.GetString() : null;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: SnakeCase) };
        request.Headers.Add("Idempotency-Key", key);
        return await Api.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in new[] { _api, _mock })
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
        }

        Api.Dispose();
        Mock.Dispose();
        try
        {
            Directory.Delete(_effectsDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static Process Start(string project, int port, Dictionary<string, string> environment)
    {
        var dll = Path.Combine(RepositoryRoot(), Path.GetDirectoryName(project)!, "bin", Configuration(), "net10.0", Path.GetFileName(project) + ".dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("Build the solution first.", dll);
        }

        var start = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitHealthyAsync(HttpClient client)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                using var response = await client.GetAsync("/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"{client.BaseAddress} never became healthy.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Configuration() =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
