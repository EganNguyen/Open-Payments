using Npgsql;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

public abstract record ResolutionResult
{
    public sealed record Applied(PaymentView Payment) : ResolutionResult;

    public sealed record AlreadyApplied(PaymentView Payment) : ResolutionResult;

    public sealed record SelfApproval : ResolutionResult;

    public sealed record NotProposed : ResolutionResult;

    public sealed record StateChanged(PaymentView Payment) : ResolutionResult;
}

/// <summary>
/// Task C5: apply an approved console resolution. The resolution row is locked, the approver is
/// checked against the proposer, and the C3 command is applied through the same in-transaction
/// path as every other writer. State, ledger, outbox, the resolution row and its audit event
/// commit together, so a crash leaves either all of it or none.
/// </summary>
public sealed class ResolutionStore(PaymentStore payments, TxRunner runner)
{
    /// <summary>Test-only failpoint after the writes and before COMMIT.</summary>
    public Action? BeforeCommit { get; set; }

    public async Task<ResolutionResult> ApplyApprovedAsync(
        Guid resolutionId,
        Guid approverStaffId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        ResolutionRow row;
        await using (var query = new NpgsqlCommand(
            """
            select payment_id, attempt_id, refund_id, kind, outcome, state, proposed_by
              from console.review_resolutions
             where id = @id
             for update
            """,
            connection,
            transaction))
        {
            query.Parameters.AddWithValue("id", resolutionId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new ResolutionResult.NotProposed();
            }

            row = new ResolutionRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetGuid(6));
        }

        if (row.State == "applied")
        {
            var applied = await LoadAsync(connection, transaction, row.PaymentId, cancellationToken);
            return applied is null
                ? new ResolutionResult.NotProposed()
                : new ResolutionResult.AlreadyApplied(applied);
        }

        if (row.State != "proposed")
        {
            return new ResolutionResult.NotProposed();
        }

        if (approverStaffId == row.ProposedBy)
        {
            return new ResolutionResult.SelfApproval();
        }

        var merchant = await MerchantAsync(connection, transaction, row.PaymentId, cancellationToken);
        if (merchant is null)
        {
            return new ResolutionResult.NotProposed();
        }

        var current = await PaymentStore.LoadPaymentAsync(
            new PaymentId(row.PaymentId), merchant.Value, cancellationToken, connection, transaction);
        if (current is null)
        {
            return new ResolutionResult.NotProposed();
        }

        var command = ToCommand(row, now);
        if (command is null)
        {
            return new ResolutionResult.StateChanged(current);
        }

        var appliedResult = await payments.ApplyInTransactionAsync(
            connection,
            transaction,
            new PaymentId(row.PaymentId),
            merchant.Value,
            command,
            null,
            cancellationToken);
        if (appliedResult is not ApplyResult.Applied done)
        {
            var moved = current;
            if (appliedResult is ApplyResult.AlreadyMoved already)
            {
                moved = already.Payment;
            }
            else if (appliedResult is ApplyResult.Rejected rejected)
            {
                moved = rejected.Payment;
            }

            return new ResolutionResult.StateChanged(moved);
        }

        await using (var update = new NpgsqlCommand(
            """
            update console.review_resolutions
               set state = 'applied', approved_by = @approver, decided_at = @now
             where id = @id and state = 'proposed'
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("approver", approverStaffId);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", resolutionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return new ResolutionResult.AlreadyApplied(done.Payment);
            }
        }

        await using (var audit = new NpgsqlCommand(
            """
            insert into console.audit_log
              (staff_id, phase, action, payment_id, refund_id, amount_minor, currency, reason, result)
            values (@staff, 'event', 'resolution.applied', @payment, @refund, @amount, @currency, @reason, 'applied')
            """,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue("staff", approverStaffId);
            audit.Parameters.AddWithValue("payment", row.PaymentId);
            audit.Parameters.AddWithValue(
                "refund",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)row.RefundId ?? DBNull.Value);
            audit.Parameters.AddWithValue("amount", done.Payment.Amount.Minor);
            audit.Parameters.AddWithValue("currency", done.Payment.Amount.Currency.Value);
            audit.Parameters.AddWithValue("reason", row.Outcome);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        BeforeCommit?.Invoke();
        await transaction.CommitAsync(cancellationToken);
        return new ResolutionResult.Applied(done.Payment);
    }

    private async Task<PaymentView?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        var merchant = await MerchantAsync(connection, transaction, paymentId, cancellationToken);
        return merchant is null
            ? null
            : await PaymentStore.LoadPaymentAsync(
                new PaymentId(paymentId), merchant.Value, cancellationToken, connection, transaction);
    }

    private static async Task<MerchantId?> MerchantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select merchant_id from payments where id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", paymentId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid merchant ? new MerchantId(merchant) : null;
    }

    private static PaymentCommand? ToCommand(ResolutionRow row, DateTimeOffset now) =>
        (row.Kind, row.Outcome) switch
        {
            ("payment", "authorize") =>
                new PaymentCommand.ManualResolution(PaymentResolution.Authorize, null, now),
            ("payment", "awaiting_method") =>
                new PaymentCommand.ManualResolution(PaymentResolution.AwaitingMethod, null, now),
            ("payment", "capture") =>
                new PaymentCommand.ManualResolution(PaymentResolution.Capture, null, now),
            ("refund", "confirm") when row.RefundId is { } refund =>
                new PaymentCommand.ManualRefundResolution(
                    new RefundId(refund), RefundResolution.Confirm, now),
            ("refund", "release") when row.RefundId is { } refund =>
                new PaymentCommand.ManualRefundResolution(
                    new RefundId(refund), RefundResolution.Release, now),
            ("late_reversal", "reversed_at_psp") when row.AttemptId is { } attempt =>
                new PaymentCommand.LateApprovalReversed(new AttemptId(attempt), now),
            _ => null,
        };

    private sealed record ResolutionRow(
        Guid PaymentId,
        Guid? AttemptId,
        Guid? RefundId,
        string Kind,
        string Outcome,
        string State,
        Guid ProposedBy);
}
