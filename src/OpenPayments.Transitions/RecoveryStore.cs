using Npgsql;
using OpenPayments.Domain;

namespace OpenPayments.Transitions;

public sealed record RecoveryItem(
    OperationRef Operation,
    PaymentId Payment,
    MerchantId Merchant,
    Guid PspAccountId,
    string Psp,
    AttemptState? AttemptState,
    string? PspReference,
    DateTimeOffset CreatedAt,
    Money Amount,
    string? MethodRef,
    string? MethodType);

/// <summary>
/// Claims unresolved operations by lease (D62): a short transaction selects due rows with
/// <c>for update skip locked</c>, sets the next lease and commits. No PSP call holds a lock.
/// </summary>
public sealed class RecoveryStore(TxRunner runner)
{
    public async Task<IReadOnlyList<RecoveryItem>> ClaimAsync(
        DateTimeOffset now, int batch, TimeSpan lease, TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var items = new List<RecoveryItem>();

        await using (var attempts = new NpgsqlCommand(
            """
            with due as (
              select attempt_id, payment_id, merchant_id, psp_account_id, psp,
                     operation, state, psp_reference, created_at, method_ref, method_type
              from payment_attempts
              where state in ('PENDING','UNKNOWN')
                and created_at < @cutoff
                and coalesce(next_recovery_at, '-infinity'::timestamptz) <= @now
              order by created_at
              limit @batch
              for update skip locked)
            update payment_attempts a
               set next_recovery_at = @lease, recovery_attempts = a.recovery_attempts + 1
              from due
             where a.payment_id = due.payment_id and a.attempt_id = due.attempt_id
            returning a.attempt_id, a.payment_id, a.merchant_id, a.psp_account_id, a.psp,
                      a.operation, a.state, a.psp_reference, a.created_at, a.method_ref, a.method_type,
                      (select amount_minor from payments p where p.id = a.payment_id),
                      (select currency from payments p where p.id = a.payment_id)
            """,
            connection,
            transaction))
        {
            attempts.Parameters.AddWithValue("now", now);
            attempts.Parameters.AddWithValue("lease", now + lease);
            attempts.Parameters.AddWithValue("cutoff", now - attemptTimeout);
            attempts.Parameters.AddWithValue("batch", batch);
            await using var reader = await attempts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var operation = StateText.Operation(reader.GetString(5));
                var payment = new PaymentId(reader.GetGuid(1));
                items.Add(new RecoveryItem(
                    new OperationRef.Attempt(new AttemptId(reader.GetGuid(0)), operation),
                    payment,
                    new MerchantId(reader.GetGuid(2)),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    StateText.Attempt(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDateTime(8),
                    new Money(reader.GetInt64(11), CurrencyCode.Parse(reader.GetString(12).Trim())),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
                _ = payment;
            }
        }

        await using (var refunds = new NpgsqlCommand(
            """
            with due as (
              select id, payment_id, merchant_id, psp_account_id, psp, state, psp_reference, created_at
              from refunds
              where state in ('PENDING','UNKNOWN')
                and created_at < @cutoff
                and coalesce(next_recovery_at, '-infinity'::timestamptz) <= @now
              order by created_at
              limit @batch
              for update skip locked)
            update refunds r
               set next_recovery_at = @lease, recovery_attempts = r.recovery_attempts + 1
              from due
             where r.id = due.id
            returning r.id, r.payment_id, r.merchant_id, r.psp_account_id, r.psp,
                      r.state, r.psp_reference, r.created_at, r.amount_minor,
                      (select currency from payments p where p.id = r.payment_id)
            """,
            connection,
            transaction))
        {
            refunds.Parameters.AddWithValue("now", now);
            refunds.Parameters.AddWithValue("lease", now + lease);
            refunds.Parameters.AddWithValue("cutoff", now - attemptTimeout);
            refunds.Parameters.AddWithValue("batch", batch);
            await using var reader = await refunds.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RecoveryItem(
                    new OperationRef.Refund(new RefundId(reader.GetGuid(0))),
                    new PaymentId(reader.GetGuid(1)),
                    new MerchantId(reader.GetGuid(2)),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    null,
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetDateTime(7),
                    new Money(reader.GetInt64(8), CurrencyCode.Parse(reader.GetString(9).Trim())),
                    null,
                    null));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public async Task MarkInquiryAsync(
        OperationRef operation, PaymentId payment, string result, CancellationToken cancellationToken)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = operation switch
        {
            OperationRef.Attempt attempt => new NpgsqlCommand(
                """
                update payment_attempts
                   set last_inquiry_at = now(), last_inquiry_result = @result
                 where payment_id = @payment and attempt_id = @attempt
                """,
                connection),
            OperationRef.Refund refund => new NpgsqlCommand(
                "update refunds set last_inquiry_at = now(), last_inquiry_result = @result where id = @refund",
                connection),
            _ => throw new InvalidOperationException("Unknown operation."),
        };
        command.Parameters.AddWithValue("result", result);
        if (operation is OperationRef.Attempt a)
        {
            command.Parameters.AddWithValue("payment", payment.Value);
            command.Parameters.AddWithValue("attempt", a.Id.Value);
        }
        else if (operation is OperationRef.Refund r)
        {
            command.Parameters.AddWithValue("refund", r.Id.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
