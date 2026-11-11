using Npgsql;

namespace OpenPayments.Console.Data;

public sealed record ReviewCase(
    Guid PaymentId,
    string PaymentState,
    long AmountMinor,
    string Currency,
    Guid? AttemptId,
    string? AttemptOperation,
    Guid? RefundId,
    long? RefundAmountMinor,
    Guid? OpenProposalId);

public sealed record ProposalView(
    Guid Id,
    Guid PaymentId,
    Guid? AttemptId,
    Guid? RefundId,
    string Kind,
    string Outcome,
    string PspAnswer,
    string Evidence,
    string State,
    Guid ProposedBy,
    string PaymentState,
    long AmountMinor,
    string Currency);

public interface IResolutionReader
{
    Task<ReviewCase?> FindCaseAsync(Guid paymentId, Guid? attemptId, Guid? refundId, CancellationToken ct);

    Task<ProposalView?> FindProposalAsync(Guid id, CancellationToken ct);
}

/// <summary>Reads one review case from the replica, or one proposal for the decision page.</summary>
public sealed class ResolutionReader(NpgsqlDataSource replica) : IResolutionReader
{
    public async Task<ReviewCase?> FindCaseAsync(
        Guid paymentId,
        Guid? attemptId,
        Guid? refundId,
        CancellationToken ct)
    {
        await using var command = replica.CreateCommand(
            """
            select p.state, p.amount_minor, p.currency,
                   a.attempt_id, a.operation, a.amount_minor,
                   f.id, f.amount_minor,
                   (select r.id from console.review_resolutions r
                     where r.payment_id = p.id and r.state = 'proposed'
                     order by r.created_at limit 1)
              from payments p
              left join lateral (
                select attempt_id, operation, amount_minor from payment_attempts
                 where payment_id = p.id and (@attempt is null or attempt_id = @attempt)
                 order by created_at desc limit 1) a on true
              left join lateral (
                select id, amount_minor from refunds
                 where payment_id = p.id and (@refund is null or id = @refund)
                 order by created_at desc limit 1) f on true
             where p.id = @payment
            """);
        command.Parameters.AddWithValue("payment", paymentId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("attempt", attemptId) { DataTypeName = "uuid" });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("refund", refundId) { DataTypeName = "uuid" });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new ReviewCase(
                paymentId,
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2).Trim(),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ReadTimeoutException(exception);
        }
    }

    public async Task<ProposalView?> FindProposalAsync(Guid id, CancellationToken ct)
    {
        await using var command = replica.CreateCommand(
            """
            select r.id, r.payment_id, r.attempt_id, r.refund_id, r.kind, r.outcome,
                   r.psp_answer, r.evidence, r.state, r.proposed_by,
                   p.state, p.amount_minor, p.currency
              from console.review_resolutions r
              join payments p on p.id = r.payment_id
             where r.id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ProposalView(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetGuid(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetString(12).Trim())
            : null;
    }
}
