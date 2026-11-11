using Npgsql;

namespace OpenPayments.Console.Data;

public enum ResolutionKind
{
    Payment,
    Refund,
    LateReversal,
}

public enum ResolutionOutcome
{
    Authorize,
    AwaitingMethod,
    Capture,
    Confirm,
    Release,
    ReversedAtPsp,
}

public sealed record NewResolution(
    Guid PaymentId,
    Guid? AttemptId,
    Guid? RefundId,
    ResolutionKind Kind,
    ResolutionOutcome Outcome,
    string PspAnswer,
    string Evidence,
    Guid ProposedBy);

public abstract record ProposalResult
{
    public sealed record Created(Guid Id) : ProposalResult;

    public sealed record AlreadyOpen(Guid ExistingId) : ProposalResult;

    public sealed record Invalid(string Reason) : ProposalResult;
}

public interface IResolutionWriter
{
    Task<ProposalResult> ProposeAsync(NewResolution resolution, CancellationToken ct);

    Task<bool> RejectAsync(Guid id, Guid approverId, string reason, CancellationToken ct);
}

/// <summary>
/// Writes the console's own review tables. It never touches money: approving goes through the
/// payment API's internal route, and the state, ledger and audit commit in that route's store.
/// </summary>
public sealed class ResolutionWriter(NpgsqlDataSource primary) : IResolutionWriter
{
    public async Task<ProposalResult> ProposeAsync(NewResolution resolution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resolution.PspAnswer)
            || string.IsNullOrWhiteSpace(resolution.Evidence))
        {
            return new ProposalResult.Invalid("A PSP answer and evidence are required.");
        }

        if (resolution.Kind == ResolutionKind.Refund && resolution.RefundId is null)
        {
            return new ProposalResult.Invalid("A refund case needs its refund.");
        }

        if (resolution.Kind == ResolutionKind.LateReversal && resolution.AttemptId is null)
        {
            return new ProposalResult.Invalid("A late reversal needs its attempt.");
        }

        var id = Guid.CreateVersion7();
        try
        {
            await using var command = primary.CreateCommand(
                """
                insert into console.review_resolutions
                  (id, payment_id, attempt_id, refund_id, kind, outcome, psp_answer, evidence,
                   state, proposed_by)
                values (@id, @payment, @attempt, @refund, @kind, @outcome, @answer, @evidence,
                        'proposed', @proposer)
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("payment", resolution.PaymentId);
            command.Parameters.Add(new NpgsqlParameter<Guid?>("attempt", resolution.AttemptId) { DataTypeName = "uuid" });
            command.Parameters.Add(new NpgsqlParameter<Guid?>("refund", resolution.RefundId) { DataTypeName = "uuid" });
            command.Parameters.AddWithValue("kind", KindText(resolution.Kind));
            command.Parameters.AddWithValue("outcome", OutcomeText(resolution.Outcome));
            command.Parameters.AddWithValue("answer", resolution.PspAnswer);
            command.Parameters.AddWithValue("evidence", resolution.Evidence);
            command.Parameters.AddWithValue("proposer", resolution.ProposedBy);
            await command.ExecuteNonQueryAsync(ct);
            return new ProposalResult.Created(id);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            var existing = await OpenProposalAsync(resolution.PaymentId, ct);
            return existing is null
                ? new ProposalResult.Invalid("A proposal is already open.")
                : new ProposalResult.AlreadyOpen(existing.Value);
        }
    }

    public async Task<bool> RejectAsync(Guid id, Guid approverId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        await using var transaction = await primary.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            update console.review_resolutions
               set state = 'rejected', approved_by = @approver, decided_at = now()
             where id = @id and state = 'proposed' and proposed_by <> @approver
            returning payment_id
            """,
            transaction);
        command.Parameters.AddWithValue("approver", approverId);
        command.Parameters.AddWithValue("id", id);
        var payment = await command.ExecuteScalarAsync(ct);
        if (payment is not Guid paymentId)
        {
            return false;
        }

        await using (var audit = new NpgsqlCommand(
            """
            insert into console.audit_log
              (staff_id, phase, action, payment_id, reason, result)
            values (@staff, 'event', 'resolution.rejected', @payment, @reason, 'rejected')
            """,
            transaction))
        {
            audit.Parameters.AddWithValue("staff", approverId);
            audit.Parameters.AddWithValue("payment", paymentId);
            audit.Parameters.AddWithValue("reason", reason);
            await audit.ExecuteNonQueryAsync(ct);
        }

        return true;
    }

    private async Task<Guid?> OpenProposalAsync(Guid paymentId, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            """
            select id from console.review_resolutions
             where payment_id = @payment and state = 'proposed'
             order by created_at limit 1
            """);
        command.Parameters.AddWithValue("payment", paymentId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is Guid id ? id : null;
    }

    public static string KindText(ResolutionKind kind) => kind switch
    {
        ResolutionKind.Payment => "payment",
        ResolutionKind.Refund => "refund",
        ResolutionKind.LateReversal => "late_reversal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string OutcomeText(ResolutionOutcome outcome) => outcome switch
    {
        ResolutionOutcome.Authorize => "authorize",
        ResolutionOutcome.AwaitingMethod => "awaiting_method",
        ResolutionOutcome.Capture => "capture",
        ResolutionOutcome.Confirm => "confirm",
        ResolutionOutcome.Release => "release",
        ResolutionOutcome.ReversedAtPsp => "reversed_at_psp",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
