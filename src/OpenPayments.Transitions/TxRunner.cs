using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenPayments.Domain;


namespace OpenPayments.Transitions;

public sealed class CommitOutcomeUnknownException : Exception
{
    public CommitOutcomeUnknownException(string reason, Exception? inner = null)
        : base("The commit outcome is unknown: " + reason, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class BusyException : Exception
{
    public BusyException(string reason, Exception? inner = null)
        : base("The database is busy: " + reason, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class TransitionMetrics
{
    private long _abandoned;
    private long _warnings;
    private long _lateSuccesses;
    private long _lateFailures;
    private long _conditionalUpdates;
    private readonly ConcurrentDictionary<string, long> _retries = new();

    public long AbandonedCommits => Interlocked.Read(ref _abandoned);

    public long CommitWarnings => Interlocked.Read(ref _warnings);

    public long LateCommitSuccesses => Interlocked.Read(ref _lateSuccesses);

    public long LateCommitFailures => Interlocked.Read(ref _lateFailures);

    public long ConditionalUpdateZeroRows => Interlocked.Read(ref _conditionalUpdates);

    public IReadOnlyDictionary<string, long> Retries => _retries;

    internal void AbandonedDelta(int delta) => Interlocked.Add(ref _abandoned, delta);

    internal void CommitWarning() => Interlocked.Increment(ref _warnings);

    internal void LateSuccess() => Interlocked.Increment(ref _lateSuccesses);

    internal void LateFailure() => Interlocked.Increment(ref _lateFailures);

    internal void ConditionalUpdateZeroRow() => Interlocked.Increment(ref _conditionalUpdates);

    internal void Retry(string sqlstate) =>
        _retries.AddOrUpdate(sqlstate, 1, static (_, count) => count + 1);
}

/// <summary>
/// The one database write path. Work runs under the request token; COMMIT never does.
/// A commit that exceeds its own limit is abandoned without a cancel (D28): the connection
/// stays held and a continuation observes the late result and then disposes it.
/// </summary>
public sealed class TxRunner : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _commitWaitLimit;
    private readonly TransitionMetrics _metrics;
    private readonly ILogger<TxRunner>? _logger;
    private readonly object _gate = new();
    private readonly List<Task> _abandonedTasks = [];

    public TxRunner(
        string connectionString,
        TimeSpan? commitWaitLimit = null,
        TransitionMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.CommandTimeout != 0)
        {
            throw new InvalidOperationException(
                "The transaction runner requires Command Timeout=0; a nonzero timeout cancels COMMIT.");
        }

        builder.TcpKeepAlive = true;
        if (builder.TcpKeepAliveTime == 0)
        {
            builder.TcpKeepAliveTime = 30;
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        if (loggerFactory is not null)
        {
            dataSourceBuilder.UseLoggerFactory(loggerFactory);
        }

        _dataSource = dataSourceBuilder.Build();
        _commitWaitLimit = commitWaitLimit ?? TimeSpan.FromSeconds(3);
        _metrics = metrics ?? new TransitionMetrics();
        _logger = loggerFactory?.CreateLogger<TxRunner>();
    }

    public NpgsqlDataSource DataSource => _dataSource;

    public TransitionMetrics Metrics => _metrics;

    public long AbandonedCommits => _metrics.AbandonedCommits;

    public async Task<T> RunAsync<T>(
        MerchantId merchant,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            NpgsqlConnection? connection = null;
            NpgsqlTransaction? transaction = null;
            var abandoned = false;
            try
            {
                try
                {
                    connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                }
                catch (NpgsqlException exception) when (exception.InnerException is TimeoutException)
                {
                    throw new BusyException("pool_wait", exception);
                }
                catch (TimeoutException exception)
                {
                    throw new BusyException("pool_wait", exception);
                }

                transaction = await connection.BeginTransactionAsync(cancellationToken);
                _logger?.LogDebug("transaction_begin");
                await SetMerchantAsync(connection, transaction, merchant, cancellationToken);

                var result = await work(connection, transaction, cancellationToken);

                var notices = new CommitNoticeState(_metrics);
                connection.Notice += notices.Handler;
                Task commit;
                try
                {
                    commit = transaction.CommitAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    connection.Notice -= notices.Handler;
                    throw new CommitOutcomeUnknownException("commit_start_failed", exception);
                }

                try
                {
                    await commit.WaitAsync(_commitWaitLimit, CancellationToken.None);
                }
                catch (TimeoutException exception)
                {
                    abandoned = true;
                    TrackAbandoned(connection, transaction, commit, notices);
                    throw new CommitOutcomeUnknownException("commit_wait_limit", exception);
                }
                catch (Exception exception)
                {
                    connection.Notice -= notices.Handler;
                    throw new CommitOutcomeUnknownException("commit_failed", exception);
                }

                connection.Notice -= notices.Handler;
                if (notices.Warned)
                {
                    throw new CommitOutcomeUnknownException("commit_warning");
                }

                _logger?.LogDebug("transaction_commit");
                return result;
            }
            catch (CommitOutcomeUnknownException)
            {
                _logger?.LogDebug("transaction_rollback");
                throw;
            }
            catch (PostgresException exception) when (
                exception.SqlState is "40P01" or "40001")
            {
                _logger?.LogDebug("transaction_rollback");
                _metrics.Retry(exception.SqlState);
                if (attempt >= 2)
                {
                    throw new BusyException("retry_exhausted", exception);
                }

                await DelayWithJitterAsync(attempt, cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == "55P03")
            {
                _logger?.LogDebug("transaction_rollback");
                throw new BusyException("lock_timeout", exception);
            }
            finally
            {
                if (!abandoned)
                {
                    if (transaction is not null)
                    {
                        await DisposeQuietlyAsync(transaction);
                    }

                    if (connection is not null)
                    {
                        await DisposeQuietlyAsync(connection);
                    }
                }
            }
        }
    }

    public async Task WaitForAbandonedAsync(CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = [.. _abandonedTasks];
        }

        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await WaitForAbandonedAsync();
        await _dataSource.DisposeAsync();
    }

    private void TrackAbandoned(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Task commit,
        CommitNoticeState notices)
    {
        _metrics.AbandonedDelta(1);
        var task = ObserveAbandonedAsync(connection, transaction, commit, notices);
        lock (_gate)
        {
            _abandonedTasks.Add(task);
        }
    }

    private async Task ObserveAbandonedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Task commit,
        CommitNoticeState notices)
    {
        try
        {
            await commit;
            _metrics.LateSuccess();
        }
        catch (Exception exception)
        {
            _metrics.LateFailure();
            Debug.WriteLine("Abandoned commit ended with " + exception.GetType().Name);
        }
        finally
        {
            connection.Notice -= notices.Handler;
            await DisposeQuietlyAsync(transaction);
            await DisposeQuietlyAsync(connection);
            _metrics.AbandonedDelta(-1);
            lock (_gate)
            {
                _abandonedTasks.RemoveAll(task => task.IsCompleted);
            }
        }
    }

    private static async Task SetMerchantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('app.merchant_id', @merchant, true), set_config('lock_timeout', '2s', true)",
            connection,
            transaction);
        command.Parameters.AddWithValue("merchant", merchant.ToString());
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task DelayWithJitterAsync(int attempt, CancellationToken cancellationToken)
    {
        var capMs = Math.Min(200, 5 * (1 << attempt));
        await Task.Delay(Random.Shared.Next(capMs + 1), cancellationToken);
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Dispose failed: " + exception.GetType().Name);
        }
    }

    private sealed class CommitNoticeState
    {
        private readonly TransitionMetrics _metrics;
        private int _warned;

        public CommitNoticeState(TransitionMetrics metrics)
        {
            _metrics = metrics;
            Handler = OnNotice;
        }

        public bool Warned => Volatile.Read(ref _warned) != 0;

        public NoticeEventHandler Handler { get; }

        private void OnNotice(object? sender, NpgsqlNoticeEventArgs args)
        {
            if (string.Equals(args.Notice.InvariantSeverity, "WARNING", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _warned, 1);
                _metrics.CommitWarning();
            }
        }
    }
}
