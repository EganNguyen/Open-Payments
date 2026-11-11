using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Api.Endpoints;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using OpenPayments.Transitions;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

[CollectionDefinition("acceptance")]
public sealed class AcceptanceCollection : ICollectionFixture<AcceptanceFixture>;

public sealed class AcceptanceFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public TestPsp Psp { get; } = new();

    public TestFraud Fraud { get; } = new();

    public LogCapture Logs { get; } = new();

    public QueryCounter Queries { get; } = new();

    public string AdminConnectionString => _container.GetConnectionString();

    public string AppConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Username = "op_app",
                Password = "op_local_only",
                CommandTimeout = 0,
                Timeout = 2,
                MaxPoolSize = 20,
            };
            return builder.ConnectionString;
        }
    }

    public MerchantId MerchantA { get; private set; }

    public MerchantId MerchantB { get; private set; }

    public Guid PspAccountA { get; private set; }

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var repo = FindRepositoryRoot();
        var migrations = Path.Combine(repo, "db", "migrations");
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("openpayments")
            .WithUsername("postgres")
            .WithPassword("op_local_only")
            .WithBindMount(migrations, "/schema/migrations")
            .Build();
        await _container.StartAsync();
        await ExecAsync(
            "do $$ begin if not exists (select from pg_roles where rolname = 'op_app') then create role op_app login password 'op_local_only'; end if; end $$; alter role op_app set lock_timeout = '200ms';");
        // D1: deadlock_timeout below the role's lock_timeout, so a deadlock is detected (40P01)
        // before a waiter times out (55P03). The server default of 1 s would invert that.
        await ExecAsync("alter system set deadlock_timeout = '100ms'");
        await ExecAsync("select pg_reload_conf()");
        await ResetAsync();

#pragma warning disable CA2000 // Factory is owned by the fixture and disposed in DisposeAsync.
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATABASE_URL", AppConnectionString);
            builder.UseSetting("INFLIGHT_CAP", "500");
            builder.UseSetting("Internal:Enabled", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TxRunner>();
                services.AddSingleton(_ => new TxRunner(
                    AppConnectionString, loggerFactory: Queries));
                services.RemoveAll<IPspAdapterFactory>();
                services.AddSingleton<AccountGate>();
                services.RemoveAll<ICallbackSecretProvider>();
                services.AddSingleton<ICallbackSecretProvider>(new FixedSecretProvider("whsec_test"));
                services.AddSingleton<IPspAdapterFactory>(sp => new ResilientPspAdapterFactory(
                    Psp, sp.GetRequiredService<AccountGate>()));
                services.RemoveAll<IFraudCheck>();
                services.AddSingleton<IFraudCheck>(Fraud);
            });
        });
#pragma warning restore CA2000
    }

    public async Task ResetAsync()
    {
        await ExecAsync("drop schema if exists console cascade; drop schema if exists public cascade; create schema public; grant usage on schema public to public;");
        await ExecAsync("create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now());");
        foreach (var name in MigrationNames())
        {
            await ExecAsync("-f", "/schema/migrations/" + name);
        }
        MerchantA = await SeedMerchantAsync("live_key_a");
        MerchantB = await SeedMerchantAsync("live_key_b");
        PspAccountA = await PspAccountAsync(MerchantA);
        Psp.Reset();
        Fraud.Reset();
        Logs.Clear();
        Queries.Reset();
        if (Factory is not null)
        {
            var store = Factory.Services.GetRequiredService<PaymentStore>();
            store.BeforeBegin = null;
            store.BeforeOutboxInsert = null;
            store.BeforePaymentUpdate = null;
            Factory.Services.GetRequiredService<PaymentMetrics>().Reset();
        }
    }

    public async Task<MerchantId> SeedMerchantAsync(string apiKey)
    {
        var merchant = new MerchantId(Guid.NewGuid());
        var pspAccount = Guid.NewGuid();
        await using var connection = await OpenAdminAsync();
        await using (var command = new NpgsqlCommand(
            """
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values (@id, @merchant, 'mock', 'live', 'test', 1000)
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", pspAccount);
            command.Parameters.AddWithValue("merchant", merchant.Value);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            "insert into api_keys (key_hash, merchant_id, mode, scopes) values (@hash, @merchant, 'live', array['payments:write','payments:read'])",
            connection))
        {
            command.Parameters.AddWithValue("hash", SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
            command.Parameters.AddWithValue("merchant", merchant.Value);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (type, moneyClass, kind) in new[]
        {
            ("auth_hold", "logical", "clearing"),
            ("auth_hold_contra", "logical", "clearing"),
            ("psp_receivable", "real", "terminal"),
            ("merchant_payable", "real", "terminal"),
            ("refund_reserved", "real", "clearing"),
            ("psp_refund_payable", "real", "terminal"),
        })
        {
            foreach (var currency in new[] { "USD", "JPY", "KWD" })
            {
                await using var command = new NpgsqlCommand(
                    """
                    insert into accounts
                      (id, merchant_id, account_type, sub_account, class, class_of_money, currency)
                    values (@id, @merchant, @type, 0, @class, @money_class, @currency)
                    """,
                    connection);
                command.Parameters.AddWithValue("id", Guid.NewGuid());
                command.Parameters.AddWithValue("merchant", merchant.Value);
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("class", kind);
                command.Parameters.AddWithValue("money_class", moneyClass);
                command.Parameters.AddWithValue("currency", currency);
                await command.ExecuteNonQueryAsync();
            }
        }

        return merchant;
    }

    public async Task<Guid> PspAccountAsync(MerchantId merchant)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select id from psp_accounts where merchant_id = @merchant limit 1",
            connection);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<string?> TextAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<string> StateAsync(Guid paymentId)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select state from payments where id = @id", connection);
        command.Parameters.AddWithValue("id", paymentId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public HttpClient Client()
    {
        var client = Factory.CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return client;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task ExecAsync(params string[] arguments)
    {
        string[] command = arguments.Length == 2 && arguments[0] == "-f"
            ? ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-f", arguments[1]]
            : ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c", arguments[0]];
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container command failed ({result.ExitCode}): {string.Join(' ', arguments)}\n{result.Stderr}");
        }
    }

    // Every migration, in the name order scripts/migrate.sh applies them.
    private static IEnumerable<string> MigrationNames() =>
        Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "db", "migrations"), "*.sql")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}

public sealed record TransactionCount(int Statements, int RoundTrips);

public sealed class QueryCounter : ILoggerProvider, ILoggerFactory
{
    private readonly object _gate = new();
    private readonly List<TransactionCount> _transactions = [];
    private long _count;
    private int _current;
    private bool _inTransaction;

    public long Count => Interlocked.Read(ref _count);

    public IReadOnlyList<TransactionCount> Transactions
    {
        get
        {
            lock (_gate)
            {
                return [.. _transactions];
            }
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        lock (_gate)
        {
            _transactions.Clear();
            _current = 0;
            _inTransaction = false;
        }
    }

    public ILogger CreateLogger(string categoryName) => new CountingLogger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Observe(string message)
    {
        if (message.Contains("transaction_begin", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _inTransaction = true;
                _current = 0;
            }
            return;
        }

        if (message.Contains("transaction_commit", StringComparison.Ordinal)
            || message.Contains("transaction_rollback", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (_inTransaction)
                {
                    _transactions.Add(new TransactionCount(_current, _current));
                    _inTransaction = false;
                    _current = 0;
                }
            }
            return;
        }

        if (message.Contains("Executing command", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _count);
            lock (_gate)
            {
                if (_inTransaction)
                {
                    _current++;
                }
            }
        }
    }

    private sealed class CountingLogger(QueryCounter owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            owner.Observe(message);
        }
    }
}

public sealed class LogCapture : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Clear()
    {
        while (_lines.TryDequeue(out _))
        {
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line) => _lines.Enqueue(line);

    private sealed class CaptureLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Add(category + ": " + formatter(state, exception));
            }
        }
    }
}

public sealed class TestFraud : IFraudCheck
{
    private int _checks;
    private FraudDecision _decision = new FraudDecision.Allow();

    public int Checks => Volatile.Read(ref _checks);

    public void Block(string code) => _decision = new FraudDecision.Block(code);

    public void Reset()
    {
        _decision = new FraudDecision.Allow();
        Interlocked.Exchange(ref _checks, 0);
    }

    public Task<FraudDecision> CheckAsync(
        MerchantId merchant, PaymentId payment, Money amount, CancellationToken ct)
    {
        Interlocked.Increment(ref _checks);
        return Task.FromResult(_decision);
    }
}

public sealed record PspCall(string Operation, string Key, long Amount);

public sealed class FixedSecretProvider(string secret) : ICallbackSecretProvider
{
    public string? Get(string? reference) => secret;
}

public sealed class TestPsp : IPspAdapterFactory, IPspAdapter
{
    private readonly object _gate = new();
    private readonly List<PspCall> _calls = [];
    private readonly Queue<PspResult> _results = [];
    private PspResult _result = new PspResult.Approved("psp_default");
    private TimeSpan _delay;
    private bool _timeout;
    private bool _supportsSale = true;
    private Exception? _throwOnce;
    private bool _unauthorized;
    private bool _rateLimitOnce;
    private bool _unavailable;

    public IReadOnlyList<PspCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    PspCapabilities IPspAdapterFactory.Capabilities(PspAccount account) =>
        MockCapabilities.Create(_supportsSale);

    public IPspAdapter For(PspAccount account) => this;

    public PspCapabilities Capabilities => MockCapabilities.Create(_supportsSale);

    public void Reset()
    {
        lock (_gate)
        {
            _calls.Clear();
            _results.Clear();
            _result = new PspResult.Approved("psp_default");
            _delay = TimeSpan.Zero;
            _timeout = false;
            _supportsSale = true;
            _throwOnce = null;
            _unauthorized = false;
            _rateLimitOnce = false;
            _unavailable = false;
        }
    }

    /// <summary>The next call, after it is recorded as sent, throws this: an answer the adapter
    /// couldn't handle (review M8).</summary>
    public void ThrowOnce(Exception exception) => _throwOnce = exception;

    private void ThrowIfScripted()
    {
        if (Interlocked.Exchange(ref _throwOnce, null) is { } exception)
        {
            throw exception;
        }
    }

    public void Next(PspResult result) => _results.Enqueue(result);

    private PspResult TakeResult() => _results.Count > 0 ? _results.Dequeue() : _result;

    public void Delay(TimeSpan delay) => _delay = delay;

    public void Timeout(bool value = true) => _timeout = value;

    public bool SupportsSale
    {
        get => _supportsSale;
        set => _supportsSale = value;
    }

    /// <summary>C51: the next authorize answers 401, before any effect.</summary>
    public bool Unauthorized
    {
        get => _unauthorized;
        set => _unauthorized = value;
    }

    /// <summary>C12: the next authorize answers 429 once, then succeeds.</summary>
    public bool RateLimitOnce
    {
        get => _rateLimitOnce;
        set => _rateLimitOnce = value;
    }

    /// <summary>C13: every authorize fails with a PSP outage.</summary>
    public bool Unavailable
    {
        get => _unavailable;
        set => _unavailable = value;
    }

    public async Task<PspResult> AuthorizeAsync(
        AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct)
    {
        await RecordAsync("authorize", attempt, amount, ct);
        if (_timeout)
        {
            throw new PspTimeoutException("scripted timeout");
        }

        if (_unavailable)
        {
            throw new PspUnavailableException("scripted outage");
        }

        if (_unauthorized)
        {
            throw new PspUnauthorizedException();
        }

        if (_rateLimitOnce)
        {
            _rateLimitOnce = false;
            throw new PspRateLimitException(TimeSpan.Zero);
        }

        return TakeResult();
    }

    public async Task<PspResult> SaleAsync(
        AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct)
    {
        await RecordAsync("sale", attempt, amount, ct);
        if (_timeout)
        {
            throw new PspTimeoutException("scripted timeout");
        }

        return TakeResult();
    }

    public Task<PspResult> CompleteActionAsync(AttemptId attempt, CancellationToken ct) =>
        Task.FromResult(_result);

    public async Task<PspResult> CaptureAsync(AttemptId attempt, Money amount, CancellationToken ct)
    {
        await RecordAsync("capture", attempt, amount, ct);
        if (_timeout)
        {
            throw new PspTimeoutException("scripted timeout");
        }

        return TakeResult();
    }

    public async Task<PspResult> VoidAsync(AttemptId attempt, AttemptId authorized, CancellationToken ct)
    {
        await RecordAsync("void", attempt, new Money(0, CurrencyCode.Usd), ct);
        if (_timeout)
        {
            throw new PspTimeoutException("scripted timeout");
        }

        return TakeResult();
    }

    public async Task<PspResult> RefundAsync(RefundId refund, Money amount, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(new PspCall("refund", refund.Value.ToString("D"), amount.Minor));
        }

        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, ct);
        }

        ThrowIfScripted();

        if (_timeout)
        {
            throw new PspTimeoutException("scripted timeout");
        }

        return TakeResult();
    }

    public Task<PspInquiry> RecoverAsync(AttemptId attempt, CancellationToken ct) =>
        Task.FromResult<PspInquiry>(new PspInquiry.Inconclusive("not_used"));

    public Task<PspInquiry> RecoverRefundAsync(RefundId refund, CancellationToken ct) =>
        Task.FromResult<PspInquiry>(new PspInquiry.Inconclusive("not_used"));

    public Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct) =>
        Task.FromResult<PspInquiry>(new PspInquiry.Inconclusive("not_used"));

    public Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct) =>
        Task.FromResult<PspInquiry>(new PspInquiry.ProvenNotCharged("cancelled"));

    private async Task RecordAsync(string operation, AttemptId attempt, Money amount, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(new PspCall(operation, attempt.Value.ToString("D"), amount.Minor));
        }

        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, ct);
        }

        ThrowIfScripted();
    }
}
