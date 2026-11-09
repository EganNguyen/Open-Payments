using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Npgsql;
using OpenPayments.Console.Presentation;

namespace OpenPayments.Console.Data;

public enum ReadFrom
{
    Replica,
    Primary,
}

public sealed record PaymentRow(
    Guid Id,
    Guid MerchantId,
    string State,
    long AmountMinor,
    string Currency,
    long CapturedMinor,
    long RefundedMinor,
    long RefundPendingMinor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AuthorizedUntil)
{
    /// <summary>The same limit the database enforces.</summary>
    public long RefundableMinor => CapturedMinor - RefundedMinor - RefundPendingMinor;
}

public sealed record PaymentQuery(
    IReadOnlyList<Guid> Merchants,
    DateTimeOffset From,
    DateTimeOffset To,
    string? State,
    long? MinAmountMinor,
    long? MaxAmountMinor,
    string? AfterCursor,
    int Limit = 50);

public sealed record PaymentPage(IReadOnlyList<PaymentRow> Rows, string? NextCursor);

/// <summary>A read ran past the read role's statement timeout.</summary>
public sealed class ReadTimeoutException(Exception inner) : Exception("The search took too long.", inner);

/// <summary>The replica can't be reached.</summary>
public sealed class ReplicaUnavailableException(Exception inner) : Exception("The replica is unavailable.", inner);

public interface IConsoleReadStore
{
    Task<PaymentRow?> FindPaymentAsync(Guid id, ReadFrom from, CancellationToken ct);

    Task<PaymentPage> ListPaymentsAsync(PaymentQuery query, CancellationToken ct);

    /// <summary>How far the replica is behind; zero on a primary; null when unreachable.</summary>
    Task<TimeSpan?> ReplicaLagAsync(CancellationToken ct);
}

public sealed class ConsoleReadStore(NpgsqlDataSource replica, NpgsqlDataSource primary) : IConsoleReadStore
{
    public const string PaymentColumns =
        "id, merchant_id, state, amount_minor, currency, captured_minor, refunded_minor, refund_pending_minor, created_at, updated_at, authorized_until";

    // Keyset paging on (created_at, id), newest first, inside the required date range, so the
    // existing payments_merchant_created index serves it. State and amount filter within it.
    public const string ListSql =
        "select " + PaymentColumns + """
         from payments
        where merchant_id = any(@merchants)
          and created_at >= @from and created_at < @to
          and (@state::text is null or state = @state)
          and (@min::bigint is null or amount_minor >= @min)
          and (@max::bigint is null or amount_minor <= @max)
          and (@after_at::timestamptz is null or (created_at, id) < (@after_at, @after_id))
        order by created_at desc, id desc
        limit @limit
        """;

    public async Task<PaymentRow?> FindPaymentAsync(Guid id, ReadFrom from, CancellationToken ct)
    {
        var source = from == ReadFrom.Replica ? replica : primary;
        try
        {
            await using var command = source.CreateCommand("select " + PaymentColumns + " from payments where id = @id");
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(reader) : null;
        }
        catch (Exception exception) when (from == ReadFrom.Replica && IsUnreachable(exception))
        {
            throw new ReplicaUnavailableException(exception);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ReadTimeoutException(exception);
        }
    }

    public async Task<PaymentPage> ListPaymentsAsync(PaymentQuery query, CancellationToken ct)
    {
        try
        {
            await using var command = replica.CreateCommand(ListSql);
            command.Parameters.AddWithValue("merchants", query.Merchants.ToArray());
            command.Parameters.AddWithValue("from", query.From);
            command.Parameters.AddWithValue("to", query.To);
            command.Parameters.Add(new NpgsqlParameter<string?>("state", query.State) { DataTypeName = "text" });
            command.Parameters.Add(new NpgsqlParameter<long?>("min", query.MinAmountMinor) { DataTypeName = "bigint" });
            command.Parameters.Add(new NpgsqlParameter<long?>("max", query.MaxAmountMinor) { DataTypeName = "bigint" });
            var after = DecodeCursor(query.AfterCursor);
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("after_at", after?.At) { DataTypeName = "timestamptz" });
            command.Parameters.Add(new NpgsqlParameter<Guid?>("after_id", after?.Id) { DataTypeName = "uuid" });
            command.Parameters.AddWithValue("limit", query.Limit + 1);

            var rows = new List<PaymentRow>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(Read(reader));
                }
            }

            string? next = null;
            if (rows.Count > query.Limit)
            {
                rows.RemoveAt(rows.Count - 1);
                next = EncodeCursor(rows[^1].CreatedAt, rows[^1].Id);
            }

            return new PaymentPage(rows, next);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ReadTimeoutException(exception);
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            throw new ReplicaUnavailableException(exception);
        }
    }

    public async Task<TimeSpan?> ReplicaLagAsync(CancellationToken ct)
    {
        try
        {
            await using var command = replica.CreateCommand(
                // A caught-up replica has replayed all it received. Its last replay time only
                // ages while the primary is idle, which isn't lag.
                """
                select case
                  when not pg_is_in_recovery() then interval '0'
                  when pg_last_wal_receive_lsn() = pg_last_wal_replay_lsn() then interval '0'
                  else coalesce(now() - pg_last_xact_replay_timestamp(), interval '0')
                end
                """);
            return (TimeSpan)(await command.ExecuteScalarAsync(ct))!;
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            return null;
        }
    }

    public static string EncodeCursor(DateTimeOffset at, Guid id) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            at.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString("N")));

    public static (DateTimeOffset At, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            return (new DateTimeOffset(long.Parse(parts[0], CultureInfo.InvariantCulture), TimeSpan.Zero), Guid.ParseExact(parts[1], "N"));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static PaymentRow Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));

    private static bool IsUnreachable(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } or System.Net.Sockets.SocketException or TimeoutException
        || exception is NpgsqlException { InnerException: System.Net.Sockets.SocketException or TimeoutException or IOException };
}

/// <summary>Search settings, all configuration with the plan's defaults.</summary>
public sealed class SearchOptions
{
    public TimeSpan DefaultRange { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan MaxRange { get; set; } = TimeSpan.FromDays(31);

    public TimeSpan LagBanner { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The merchants this deployment manages. Staff see all of them and nothing else.</summary>
    public IReadOnlyList<Guid> Merchants { get; set; } = [];

    public static IReadOnlyList<string> FilterStates => [.. StatePresentation.CommittedStates.Order(StringComparer.Ordinal)];
}
