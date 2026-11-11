using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

public sealed record SettlementMatchSummary(
    int Matched,
    int Unmatched,
    int Mismatch,
    int Duplicate,
    int Ignored);

public sealed record MissingSettlement(int Attempts, int Refunds);

public sealed record SettlementAccount(
    Guid Id,
    MerchantId MerchantId,
    string Psp,
    string Mode,
    string CredentialRef,
    int RateLimitPerSecond,
    decimal RecoveryShare);

/// <summary>
/// D18/D63: settlement lines are stored once per (account, file, line), matched by the PSP's
/// own reference, and only a matched line posts. Posting goes through the payment-transition
/// module, so a settlement never changes payment state. The ledger transaction id is UUIDv5 of
/// (account, file, line), which makes a resumed file post nothing twice.
/// </summary>
public sealed class SettlementStore
{
    /// <summary>The UUIDv5 namespace for settlement ledger transactions.</summary>
    public static readonly Guid SettlementNamespace = new("5c8f9f3a-6b0e-4e3f-9d1a-2f6c7b8d9e01");

    private readonly TxRunner _runner;
    private readonly PaymentStore _payments;

    public SettlementStore(TxRunner runner, PaymentStore payments)
    {
        _runner = runner;
        _payments = payments;
    }

    public static Guid TransactionId(Guid pspAccountId, string fileId, int lineNo) =>
        Uuid5(
            SettlementNamespace,
            pspAccountId.ToString("N", CultureInfo.InvariantCulture)
            + ":" + fileId + ":" + lineNo.ToString(CultureInfo.InvariantCulture));

    public async Task<IReadOnlyList<SettlementAccount>> AccountsAsync(CancellationToken ct)
    {
        var accounts = new List<SettlementAccount>();
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select id, merchant_id, psp, mode, credential_ref, rate_limit_per_s, recovery_share
              from psp_accounts
             order by id
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            accounts.Add(new SettlementAccount(
                reader.GetGuid(0),
                new MerchantId(reader.GetGuid(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetDecimal(6)));
        }

        return accounts;
    }

    /// <summary>Marks the file receiving unless it is already done.</summary>
    public async Task<bool> StartFileAsync(SettlementAccount account, SettlementFile file, CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            """
            insert into settlement_files
              (psp_account_id, file_id, business_date, state, received_at)
            values (@account, @file, @date, 'receiving', now())
            on conflict (psp_account_id, file_id) do nothing
            """,
            connection))
        {
            insert.Parameters.AddWithValue("account", account.Id);
            insert.Parameters.AddWithValue("file", file.FileId);
            insert.Parameters.AddWithValue("date", file.BusinessDate);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var state = new NpgsqlCommand(
            "select state from settlement_files where psp_account_id = @account and file_id = @file",
            connection);
        state.Parameters.AddWithValue("account", account.Id);
        state.Parameters.AddWithValue("file", file.FileId);
        return (string)(await state.ExecuteScalarAsync(ct))! != "done";
    }

    public async Task IngestLineAsync(
        SettlementAccount account,
        string fileId,
        SettlementLine line,
        CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            """
            insert into settlement_lines
              (psp, psp_account_id, file_id, line_no, kind, psp_reference,
               amount_minor, currency, state, received_at)
            values (@psp, @account, @file, @line, @kind, @reference,
                    @amount, @currency, 'RECEIVED', now())
            on conflict (psp_account_id, file_id, line_no) do nothing
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("psp", account.Psp);
            insert.Parameters.AddWithValue("account", account.Id);
            insert.Parameters.AddWithValue("file", fileId);
            insert.Parameters.AddWithValue("line", line.LineNo);
            insert.Parameters.AddWithValue("kind", line.Kind);
            insert.Parameters.AddWithValue("reference", (object?)line.PspReference ?? DBNull.Value);
            insert.Parameters.AddWithValue("amount", line.Amount.Minor);
            insert.Parameters.AddWithValue("currency", line.Amount.Currency.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var file = new NpgsqlCommand(
            """
            update settlement_files
               set state = 'matching', lines_total = greatest(lines_total, @line)
             where psp_account_id = @account and file_id = @file and state <> 'done'
            """,
            connection,
            transaction))
        {
            file.Parameters.AddWithValue("line", line.LineNo);
            file.Parameters.AddWithValue("account", account.Id);
            file.Parameters.AddWithValue("file", fileId);
            await file.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task CompleteFileAsync(
        SettlementAccount account,
        string fileId,
        int lineCount,
        CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            update settlement_files
               set state = 'matching', lines_total = @count
             where psp_account_id = @account and file_id = @file and state <> 'done'
            """,
            connection);
        command.Parameters.AddWithValue("count", lineCount);
        command.Parameters.AddWithValue("account", account.Id);
        command.Parameters.AddWithValue("file", fileId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishFileAsync(SettlementAccount account, string fileId, CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            update settlement_files
               set state = 'done', completed_at = now()
             where psp_account_id = @account and file_id = @file and state <> 'done'
            """,
            connection);
        command.Parameters.AddWithValue("account", account.Id);
        command.Parameters.AddWithValue("file", fileId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SettlementMatchSummary> MatchPendingAsync(
        SettlementAccount account,
        string fileId,
        CancellationToken ct)
    {
        var lines = await ReceivedLinesAsync(account.Id, fileId, ct);
        var matched = 0;
        var unmatched = 0;
        var mismatch = 0;
        var duplicate = 0;
        var ignored = 0;
        foreach (var line in lines)
        {
            var outcome = await MatchLineAsync(account, fileId, line, ct);
            switch (outcome)
            {
                case "MATCHED": matched++; break;
                case "UNMATCHED": unmatched++; break;
                case "MISMATCH": mismatch++; break;
                case "DUPLICATE": duplicate++; break;
                case "IGNORED": ignored++; break;
                default: break;
            }
        }

        return new SettlementMatchSummary(matched, unmatched, mismatch, duplicate, ignored);
    }

    /// <summary>D63: an operation with no matched line past the adapter's lag is a discrepancy.</summary>
    public async Task<MissingSettlement> FindMissingAsync(
        SettlementAccount account,
        DateTimeOffset now,
        TimeSpan lag,
        CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        var expected = now - lag;
        int attempts;
        await using (var command = new NpgsqlCommand(
            """
            insert into settlement_missing
              (psp_account_id, payment_id, attempt_id, expected_by)
            select a.psp_account_id, a.payment_id, a.attempt_id, a.created_at + @lag
              from payment_attempts a
             where a.psp_account_id = @account
               and a.state = 'SUCCEEDED'
               and a.operation in ('sale','capture')
               and a.psp_reference is not null
               and a.created_at <= @expected
               and not exists (
                 select 1 from settlement_lines s
                  where s.psp_account_id = @account
                    and s.attempt_id = a.attempt_id
                    and s.state = 'MATCHED')
            on conflict do nothing
            """,
            connection))
        {
            command.Parameters.AddWithValue("lag", lag);
            command.Parameters.AddWithValue("expected", expected);
            command.Parameters.AddWithValue("account", account.Id);
            attempts = await command.ExecuteNonQueryAsync(ct);
        }

        int refunds;
        await using (var command = new NpgsqlCommand(
            """
            insert into settlement_missing
              (psp_account_id, payment_id, refund_id, expected_by)
            select r.psp_account_id, r.payment_id, r.id, r.created_at + @lag
              from refunds r
             where r.psp_account_id = @account
               and r.state = 'SUCCEEDED'
               and r.psp_reference is not null
               and r.created_at <= @expected
               and not exists (
                 select 1 from settlement_lines s
                  where s.psp_account_id = @account
                    and s.refund_id = r.id
                    and s.state = 'MATCHED')
            on conflict do nothing
            """,
            connection))
        {
            command.Parameters.AddWithValue("lag", lag);
            command.Parameters.AddWithValue("expected", expected);
            command.Parameters.AddWithValue("account", account.Id);
            refunds = await command.ExecuteNonQueryAsync(ct);
        }

        return new MissingSettlement(attempts, refunds);
    }

    private async Task<IReadOnlyList<SettlementLineRow>> ReceivedLinesAsync(
        Guid account,
        string fileId,
        CancellationToken ct)
    {
        var lines = new List<SettlementLineRow>();
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select line_no, kind, psp_reference, amount_minor, currency, state
              from settlement_lines
             where psp_account_id = @account and file_id = @file and state = 'RECEIVED'
             order by line_no
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("file", fileId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(new SettlementLineRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4).Trim(),
                reader.GetString(5)));
        }

        return lines;
    }

    private async Task<string> MatchLineAsync(
        SettlementAccount account,
        string fileId,
        SettlementLineRow line,
        CancellationToken ct)
    {
        if (line.Kind is not ("sale" or "capture" or "refund"))
        {
            await SetLineStateAsync(account.Id, fileId, line.LineNo, "IGNORED", null, ct);
            return "IGNORED";
        }

        var operation = line.Kind == "refund"
            ? await FindRefundAsync(account.Id, line.PspReference, ct)
            : await FindAttemptAsync(account.Id, line.PspReference, ct);
        if (operation is null)
        {
            await SetLineStateAsync(account.Id, fileId, line.LineNo, "UNMATCHED", null, ct);
            return "UNMATCHED";
        }

        if (operation.AmountMinor != line.AmountMinor
            || !string.Equals(operation.Currency, line.Currency, StringComparison.Ordinal))
        {
            await SetLineStateAsync(account.Id, fileId, line.LineNo, "MISMATCH", null, ct);
            return "MISMATCH";
        }

        var duplicate = await HasMatchedLineAsync(
            account.Id, fileId, line.LineNo, operation.AttemptId, operation.RefundId, ct);
        if (duplicate)
        {
            await SetLineStateAsync(account.Id, fileId, line.LineNo, "DUPLICATE", null, ct);
            return "DUPLICATE";
        }

        var command = new PaymentCommand.SettlementMatched(
            operation.AttemptId is { } attempt ? new AttemptId(attempt) : null,
            operation.RefundId is { } refund ? new RefundId(refund) : null,
            new Money(line.AmountMinor, CurrencyCode.Parse(line.Currency)),
            TransactionId(account.Id, fileId, line.LineNo),
            DateTimeOffset.UtcNow);
        var applied = await _payments.ApplyAsync(
            new PaymentId(operation.PaymentId), command, null, ct);
        if (applied is not ApplyResult.Applied)
        {
            // The payment moved between matching and posting; the line stays RECEIVED and the
            // next tick tries again. The ledger id is fixed, so a retry can't post twice.
            return "RECEIVED";
        }

        await SetLineStateAsync(
            account.Id,
            fileId,
            line.LineNo,
            "MATCHED",
            new MatchedOperation(operation.MerchantId, operation.PaymentId, operation.AttemptId, operation.RefundId),
            ct);
        return "MATCHED";
    }

    private async Task<OperationRow?> FindAttemptAsync(
        Guid account,
        string? reference,
        CancellationToken ct)
    {
        if (reference is null)
        {
            return null;
        }

        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select a.attempt_id, a.payment_id, a.merchant_id, p.amount_minor, p.currency
              from payment_attempts a
              join payments p on p.id = a.payment_id
             where a.psp_account_id = @account
               and a.psp_reference = @reference
               and a.operation in ('sale','capture')
               and a.state = 'SUCCEEDED'
             order by a.created_at desc
             limit 1
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("reference", reference);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new OperationRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetInt64(3), reader.GetString(4).Trim())
            : null;
    }

    private async Task<OperationRow?> FindRefundAsync(
        Guid account,
        string? reference,
        CancellationToken ct)
    {
        if (reference is null)
        {
            return null;
        }

        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select r.id, r.payment_id, r.merchant_id, r.amount_minor, p.currency
              from refunds r
              join payments p on p.id = r.payment_id
             where r.psp_account_id = @account
               and r.psp_reference = @reference
               and r.state = 'SUCCEEDED'
             order by r.created_at desc
             limit 1
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("reference", reference);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new OperationRow(
                null, reader.GetGuid(1), reader.GetGuid(2),
                reader.GetInt64(3), reader.GetString(4).Trim(), reader.GetGuid(0))
            : null;
    }

    private async Task<bool> HasMatchedLineAsync(
        Guid account,
        string fileId,
        int lineNo,
        Guid? attempt,
        Guid? refund,
        CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            select exists (
              select 1 from settlement_lines
               where psp_account_id = @account and state = 'MATCHED'
                 and not (file_id = @file and line_no = @line)
                 and ((@attempt is not null and attempt_id = @attempt)
                   or (@refund is not null and refund_id = @refund)))
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("file", fileId);
        command.Parameters.AddWithValue("line", lineNo);
        command.Parameters.AddWithValue("attempt", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)attempt ?? DBNull.Value);
        command.Parameters.AddWithValue("refund", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)refund ?? DBNull.Value);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task SetLineStateAsync(
        Guid account,
        string fileId,
        int lineNo,
        string state,
        MatchedOperation? matched,
        CancellationToken ct)
    {
        await using var connection = await _runner.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            update settlement_lines
               set state = @state,
                   merchant_id = @merchant,
                   payment_id = @payment,
                   attempt_id = @attempt,
                   refund_id = @refund
             where psp_account_id = @account and file_id = @file and line_no = @line
            """,
            connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("merchant", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)matched?.MerchantId ?? DBNull.Value);
        command.Parameters.AddWithValue("payment", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)matched?.PaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)matched?.AttemptId ?? DBNull.Value);
        command.Parameters.AddWithValue("refund", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)matched?.RefundId ?? DBNull.Value);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("file", fileId);
        command.Parameters.AddWithValue("line", lineNo);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Guid Uuid5(Guid ns, string name)
    {
        var namespaceBytes = ns.ToByteArray();
        Array.Reverse(namespaceBytes, 0, 4);
        Array.Reverse(namespaceBytes, 4, 2);
        Array.Reverse(namespaceBytes, 6, 2);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var guidBytes = hash[..16];
        Array.Reverse(guidBytes, 0, 4);
        Array.Reverse(guidBytes, 4, 2);
        Array.Reverse(guidBytes, 6, 2);
        return new Guid(guidBytes);
    }

    private sealed record SettlementLineRow(
        int LineNo,
        string Kind,
        string? PspReference,
        long AmountMinor,
        string Currency,
        string State);

    private sealed record OperationRow(
        Guid? AttemptId,
        Guid PaymentId,
        Guid MerchantId,
        long AmountMinor,
        string Currency,
        Guid? RefundId = null);

    private sealed record MatchedOperation(
        Guid MerchantId,
        Guid PaymentId,
        Guid? AttemptId,
        Guid? RefundId);
}
