using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FsCheck.Xunit;
using OpenPayments.Domain;
using static OpenPayments.Domain.PaymentRules;
using Xunit;

namespace OpenPayments.Domain.Tests;

public sealed class DecideTransitionTests
{
    private static readonly PaymentId Payment = PaymentId.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly MerchantId Merchant = MerchantId.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly AttemptId Attempt = AttemptId.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly AttemptId CaptureAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000022");
    private static readonly AttemptId VoidAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000023");
    private static readonly RefundId Refund = RefundId.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly Money Hundred = new(100, CurrencyCode.Usd);

    public static IEnumerable<object[]> AllowedTransitions()
    {
        yield return new object[] { PaymentState.AwaitingMethod, new PaymentCommand.Attempt(Attempt, Now), PaymentState.PspPending };
        yield return new object[] { PaymentState.AwaitingMethod, new PaymentCommand.Sale(Attempt, Now), PaymentState.PspPending };
        yield return new object[] { PaymentState.AwaitingMethod, new PaymentCommand.Cancel(Now), PaymentState.Canceled };
        yield return new object[] { PaymentState.AwaitingMethod, Late(AttemptOperation.Authorize), PaymentState.Review };

        yield return new object[] { PaymentState.PspPending, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved), PaymentState.Authorized };
        yield return new object[] { PaymentState.PspPending, Psp(AttemptOperation.Sale, PspAnswerOutcome.Approved), PaymentState.Captured };
        yield return new object[] { PaymentState.PspPending, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Declined), PaymentState.AwaitingMethod };
        yield return new object[] { PaymentState.PspPending, Psp(AttemptOperation.Authorize, PspAnswerOutcome.RequiresAction), PaymentState.ActionRequired };
        yield return new object[] { PaymentState.PspPending, new PaymentCommand.Timeout(Attempt, AttemptOperation.Authorize, Now), PaymentState.Unknown };
        yield return new object[] { PaymentState.PspPending, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved, new Money(99, CurrencyCode.Usd)), PaymentState.Review };

        yield return new object[] { PaymentState.ActionRequired, new PaymentCommand.ActionCompleted(Attempt, Now), PaymentState.PspPending };
        yield return new object[] { PaymentState.ActionRequired, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved), PaymentState.Authorized };
        yield return new object[] { PaymentState.ActionRequired, Psp(AttemptOperation.Sale, PspAnswerOutcome.Approved), PaymentState.Captured };
        yield return new object[] { PaymentState.ActionRequired, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Declined), PaymentState.AwaitingMethod };
        yield return new object[] { PaymentState.ActionRequired, new PaymentCommand.Timeout(Attempt, AttemptOperation.Authorize, Now), PaymentState.Unknown };

        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Authorize, RecoveryOutcome.Approved), PaymentState.Authorized };
        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Sale, RecoveryOutcome.Approved), PaymentState.Captured };
        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Authorize, RecoveryOutcome.Declined), PaymentState.AwaitingMethod };
        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Authorize, RecoveryOutcome.ProvenNotCharged), PaymentState.AwaitingMethod };
        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Authorize, RecoveryOutcome.RequiresAction), PaymentState.ActionRequired };
        yield return new object[] { PaymentState.Unknown, Recovery(AttemptOperation.Authorize, RecoveryOutcome.Approved, new Money(99, CurrencyCode.Usd)), PaymentState.Review };

        yield return new object[] { PaymentState.Authorized, new PaymentCommand.CaptureAnswer(CaptureAttempt, true, null, null, false, Now), PaymentState.Captured };
        yield return new object[] { PaymentState.Authorized, new PaymentCommand.CaptureAnswer(CaptureAttempt, true, new Money(99, CurrencyCode.Usd), null, false, Now), PaymentState.Review };
        yield return new object[] { PaymentState.Authorized, new PaymentCommand.VoidAnswer(VoidAttempt, true, false, null, Now), PaymentState.Voided };
        yield return new object[] { PaymentState.Authorized, new PaymentCommand.Expiry(Now), PaymentState.Expired };

        yield return new object[] { PaymentState.Review, new PaymentCommand.ManualResolution(PaymentResolution.Authorize, null, Now), PaymentState.Authorized };
        yield return new object[] { PaymentState.Review, new PaymentCommand.ManualResolution(PaymentResolution.AwaitingMethod, null, Now), PaymentState.AwaitingMethod };
        yield return new object[] { PaymentState.Review, new PaymentCommand.ManualResolution(PaymentResolution.Capture, null, Now), PaymentState.Captured };

        yield return new object[] { PaymentState.Captured, new PaymentCommand.RefundConfirm(Refund, null, Now), PaymentState.PartiallyRefunded };
        yield return new object[] { PaymentState.PartiallyRefunded, new PaymentCommand.RefundConfirm(Refund, null, Now), PaymentState.Refunded };

        yield return new object[] { PaymentState.Captured, new PaymentCommand.Cancel(Now), null! };
        yield return new object[] { PaymentState.Refunded, new PaymentCommand.Attempt(Attempt, Now), null! };
        yield return new object[] { PaymentState.AwaitingMethod, Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved), null! };
        yield return new object[] { PaymentState.Canceled, new PaymentCommand.Expiry(Now), null! };
        yield return new object[] { PaymentState.Authorized, new PaymentCommand.RefundReserve(Refund, Hundred, Now), null! };
        yield return new object[] { PaymentState.PspPending, new PaymentCommand.Capture(CaptureAttempt, Hundred, Now), null! };
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void U2_only_listed_transitions_are_accepted(
        PaymentState from,
        PaymentCommand command,
        PaymentState? expected)
    {
        var snapshot = command switch
        {
            PaymentCommand.RefundConfirm => Snapshot(
                from,
                captured: 100,
                refunded: from == PaymentState.PartiallyRefunded ? 30 : 0,
                pending: from == PaymentState.PartiallyRefunded ? 70 : from == PaymentState.Captured ? 30 : 0,
                refundState: RefundState.Pending,
                refundAmount: from == PaymentState.PartiallyRefunded ? 70 : 30),
            _ => Snapshot(from),
        };

        var result = Decide(snapshot, command, RuleVersion.V1);

        if (expected is null)
        {
            Assert.IsNotType<TransitionResult.Accepted>(result);
            return;
        }

        var accepted = Assert.IsType<TransitionResult.Accepted>(result);
        Assert.Equal(expected, accepted.NewState);
    }

    [Fact]
    public void U2_every_lifecycle_committed_row_is_covered()
    {
        var csv = File.ReadAllLines("lifecycle-transitions.csv")
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(','))
            .Select(columns => (From: columns[0], To: columns[1]))
            .ToList();
        var covered = new HashSet<(string, string)>
        {
            ("*", "AWAITING_METHOD"),
            ("*", "PSP_PENDING"),
            ("AWAITING_METHOD", "PSP_PENDING"),
            ("AWAITING_METHOD", "CANCELED"),
            ("AWAITING_METHOD", "REVIEW"),
            ("PSP_PENDING", "AUTHORIZED"),
            ("PSP_PENDING", "CAPTURED"),
            ("PSP_PENDING", "AWAITING_METHOD"),
            ("PSP_PENDING", "ACTION_REQUIRED"),
            ("PSP_PENDING", "UNKNOWN"),
            ("PSP_PENDING", "REVIEW"),
            ("ACTION_REQUIRED", "PSP_PENDING"),
            ("ACTION_REQUIRED", "AUTHORIZED"),
            ("ACTION_REQUIRED", "CAPTURED"),
            ("ACTION_REQUIRED", "AWAITING_METHOD"),
            ("ACTION_REQUIRED", "UNKNOWN"),
            ("UNKNOWN", "AUTHORIZED"),
            ("UNKNOWN", "CAPTURED"),
            ("UNKNOWN", "AWAITING_METHOD"),
            ("UNKNOWN", "ACTION_REQUIRED"),
            ("UNKNOWN", "REVIEW"),
            ("AUTHORIZED", "CAPTURED"),
            ("AUTHORIZED", "REVIEW"),
            ("AUTHORIZED", "VOIDED"),
            ("AUTHORIZED", "EXPIRED"),
            ("REVIEW", "AUTHORIZED"),
            ("REVIEW", "AWAITING_METHOD"),
            ("REVIEW", "CAPTURED"),
            ("CAPTURED", "PARTIALLY_REFUNDED"),
            ("CAPTURED", "REFUNDED"),
            ("PARTIALLY_REFUNDED", "REFUNDED"),
        };

        foreach (var row in csv)
        {
            Assert.Contains(row, covered);
        }
    }

    [Fact]
    public void Create_without_method_waits_and_create_with_method_starts_an_attempt()
    {
        var waiting = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                EmptySnapshot(),
                new PaymentCommand.CreatePayment(Payment, Merchant, Hundred, false, AttemptOperation.Authorize, Now),
                RuleVersion.V1));
        Assert.Equal(PaymentState.AwaitingMethod, waiting.NewState);
        Assert.Null(waiting.Attempt);

        var started = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                EmptySnapshot(),
                new PaymentCommand.CreatePayment(Payment, Merchant, Hundred, true, AttemptOperation.Sale, Now),
                RuleVersion.V1));
        Assert.Equal(PaymentState.PspPending, started.NewState);
        Assert.Equal(AttemptOperation.Sale, started.Attempt!.Operation);
        Assert.Equal(AttemptState.Pending, started.Attempt.State);
    }

    [Fact]
    public void Late_approval_on_failed_attempt_goes_to_review_with_no_ledger()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(Snapshot(PaymentState.AwaitingMethod), Late(AttemptOperation.Authorize), RuleVersion.V1));

        Assert.Equal(PaymentState.Review, accepted.NewState);
        Assert.Equal(AttemptState.Review, accepted.Attempt!.State);
        Assert.Empty(accepted.Ledger);
        Assert.Empty(accepted.Events);
    }

    [Fact]
    public void Amount_mismatch_goes_to_review_with_no_ledger()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.PspPending),
                Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved, new Money(99, CurrencyCode.Usd)),
                RuleVersion.V1));

        Assert.Equal(PaymentState.Review, accepted.NewState);
        Assert.Equal(AttemptState.Review, accepted.Attempt!.State);
        Assert.Empty(accepted.Ledger);
        Assert.Empty(accepted.Events);
    }

    [Fact]
    public void Attempt_cap_rejects_eleventh()
    {
        var result = Decide(
            Snapshot(PaymentState.AwaitingMethod, attemptCount: 10),
            new PaymentCommand.Attempt(Attempt, Now),
            RuleVersion.V1);

        var rejected = Assert.IsType<TransitionResult.Rejected>(result);
        Assert.Equal("attempt_cap_reached", rejected.ReasonCode);
    }

    [Fact]
    public void D27_inconclusive_answer_leaves_attempt_unknown()
    {
        var result = Decide(
            Snapshot(PaymentState.Unknown, attemptState: AttemptState.Unknown),
            Recovery(AttemptOperation.Authorize, RecoveryOutcome.Inconclusive),
            RuleVersion.V1);

        if (result is TransitionResult.Accepted accepted)
        {
            Assert.Equal(PaymentState.Unknown, accepted.NewState);
            Assert.Equal(AttemptState.Unknown, accepted.Attempt!.State);
            Assert.Empty(accepted.Ledger);
        }
        else
        {
            Assert.IsType<TransitionResult.NoOp>(result);
        }
    }

    [Fact]
    public void D27_only_proof_or_decline_fails_an_attempt()
    {
        var proven = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Unknown, attemptState: AttemptState.Unknown),
                Recovery(AttemptOperation.Authorize, RecoveryOutcome.ProvenNotCharged),
                RuleVersion.V1));
        Assert.Equal(AttemptState.Failed, proven.Attempt!.State);

        var declined = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Unknown, attemptState: AttemptState.Unknown),
                Recovery(AttemptOperation.Authorize, RecoveryOutcome.Declined),
                RuleVersion.V1));
        Assert.Equal(AttemptState.Failed, declined.Attempt!.State);

        var inconclusive = Decide(
            Snapshot(PaymentState.Unknown, attemptState: AttemptState.Unknown),
            Recovery(AttemptOperation.Authorize, RecoveryOutcome.Inconclusive),
            RuleVersion.V1);
        Assert.DoesNotContain(
            AttemptState.Failed,
            ResultAttemptStates(inconclusive));
    }

    [Fact]
    public void D29_sale_approval_captures_with_no_hold()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.PspPending, AttemptOperation.Sale),
                Psp(AttemptOperation.Sale, PspAnswerOutcome.Approved),
                RuleVersion.V1));

        Assert.Equal(PaymentState.Captured, accepted.NewState);
        Assert.DoesNotContain(accepted.Ledger, line => line.AccountType.StartsWith("auth_hold", StringComparison.Ordinal));
        Assert.Contains(accepted.Ledger, line => line.AccountType == Posting.PspReceivable);
        Assert.Contains(accepted.Ledger, line => line.AccountType == Posting.MerchantPayable);
        Assert.Contains("payment.captured", accepted.Events.Select(outbox => outbox.EventType));
    }

    [Fact]
    public void D32_same_input_gives_same_event_and_ledger_ids()
    {
        var snapshot = Snapshot(PaymentState.PspPending);
        var command = Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved);
        var first = Assert.IsType<TransitionResult.Accepted>(Decide(snapshot, command, RuleVersion.V1));
        var second = Assert.IsType<TransitionResult.Accepted>(Decide(snapshot, command, RuleVersion.V1));

        Assert.Equal(
            first.Ledger.Select(line => line.TransactionId),
            second.Ledger.Select(line => line.TransactionId));
        Assert.Equal(
            first.Events.Select(outbox => outbox.EventId),
            second.Events.Select(outbox => outbox.EventId));
    }

    [Fact]
    public void D32_event_seq_rises_by_one_only_when_an_event_is_written()
    {
        var authorized = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.PspPending, eventSeq: 5),
                Psp(AttemptOperation.Authorize, PspAnswerOutcome.Approved),
                RuleVersion.V1));
        Assert.All(authorized.Events, outbox => Assert.Equal(6, outbox.Sequence));

        var timedOut = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.PspPending, eventSeq: 5),
                new PaymentCommand.Timeout(Attempt, AttemptOperation.Authorize, Now),
                RuleVersion.V1));
        Assert.Empty(timedOut.Events);
    }

    [Fact]
    public void D33_fraud_block_fails_attempt_with_no_psp_step()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.AwaitingMethod),
                new PaymentCommand.FraudBlock(Attempt, "fraud_blocked", Now),
                RuleVersion.V1));

        Assert.Equal(PaymentState.AwaitingMethod, accepted.NewState);
        Assert.Equal(AttemptState.Failed, accepted.Attempt!.State);
        Assert.Equal("fraud_blocked", accepted.Attempt.FailureCode);
        Assert.Empty(accepted.Ledger);
        Assert.Contains("payment.attempt_failed", accepted.Events.Select(outbox => outbox.EventType));
    }

    [Fact]
    public void D37_void_releases_hold_and_ends_voided()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Authorized, attemptState: AttemptState.Succeeded),
                new PaymentCommand.VoidAnswer(VoidAttempt, true, false, null, Now),
                RuleVersion.V1));

        Assert.Equal(PaymentState.Voided, accepted.NewState);
        Assert.Equal(AttemptState.Succeeded, accepted.Attempt!.State);
        Assert.Equal(
            0,
            NetAuthHold(Posting.AuthorizationHold(Attempt, Hundred).Concat(accepted.Ledger)));
        Assert.Contains("payment.voided", accepted.Events.Select(outbox => outbox.EventType));
    }

    [Fact]
    public void D37_expiry_releases_hold_and_ends_expired()
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Authorized, attemptState: AttemptState.Succeeded),
                new PaymentCommand.Expiry(Now),
                RuleVersion.V1));

        Assert.Equal(PaymentState.Expired, accepted.NewState);
        Assert.Equal(
            0,
            NetAuthHold(Posting.AuthorizationHold(Attempt, Hundred).Concat(accepted.Ledger)));
        Assert.Contains("payment.expired", accepted.Events.Select(outbox => outbox.EventType));
    }

    [Fact]
    public void D37_expiry_is_refused_while_capture_is_live()
    {
        var result = Decide(
            Snapshot(PaymentState.Authorized, attemptState: AttemptState.Succeeded, liveCapture: true),
            new PaymentCommand.Expiry(Now),
            RuleVersion.V1);

        Assert.IsType<TransitionResult.Rejected>(result);
    }

    [Fact]
    public void D37_late_approval_requests_a_void_except_for_a_sale()
    {
        var authorize = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.AwaitingMethod),
                Late(AttemptOperation.Authorize),
                RuleVersion.V1));
        Assert.Equal(Attempt, authorize.Attempt!.VoidTargetAttemptId);

        var sale = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.AwaitingMethod),
                Late(AttemptOperation.Sale),
                RuleVersion.V1));
        Assert.Null(sale.Attempt!.VoidTargetAttemptId);
        Assert.Equal(PaymentState.Review, sale.NewState);
    }

    [Fact]
    public void CK7_invariant_auth_hold_is_zero_in_every_final_state()
    {
        var cancel = Assert.IsType<TransitionResult.Accepted>(
            Decide(Snapshot(PaymentState.AwaitingMethod), new PaymentCommand.Cancel(Now), RuleVersion.V1));
        Assert.Equal(0, NetAuthHold(cancel.Ledger));

        var voided = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Authorized, attemptState: AttemptState.Succeeded),
                new PaymentCommand.VoidAnswer(VoidAttempt, true, false, null, Now),
                RuleVersion.V1));
        Assert.Equal(
            0,
            NetAuthHold(Posting.AuthorizationHold(Attempt, Hundred).Concat(voided.Ledger)));

        var expired = Assert.IsType<TransitionResult.Accepted>(
            Decide(
                Snapshot(PaymentState.Authorized, attemptState: AttemptState.Succeeded),
                new PaymentCommand.Expiry(Now),
                RuleVersion.V1));
        Assert.Equal(
            0,
            NetAuthHold(Posting.AuthorizationHold(Attempt, Hundred).Concat(expired.Ledger)));
    }

    [Fact]
    public void Decide_project_references_no_io()
    {
        var references = typeof(PaymentRules).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name == "System.IO");
    }

    [Fact]
    public void Golden_cases_match()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "golden");
        Assert.True(Directory.Exists(directory));
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            };
            var golden = JsonSerializer.Deserialize<GoldenCase>(File.ReadAllText(file), options)!;
            var result = Decide(golden.BuildSnapshot(), golden.BuildCommand(), RuleVersion.V1);
            var accepted = Assert.IsType<TransitionResult.Accepted>(result);
            Assert.Equal(golden.ExpectedState, accepted.NewState);
            Assert.Equal(
                golden.ExpectedLedger.OrderBy(value => value, StringComparer.Ordinal),
                accepted.Ledger
                    .Select(line => $"{line.AccountType}:{line.Direction}:{line.Amount.Minor}")
                    .OrderBy(value => value, StringComparer.Ordinal));
            Assert.Equal(
                golden.ExpectedEvents.OrderBy(value => value, StringComparer.Ordinal),
                accepted.Events.Select(outbox => outbox.EventType).OrderBy(value => value, StringComparer.Ordinal));
        }
    }

    private static PaymentSnapshot EmptySnapshot() =>
        new(Payment, Merchant, PaymentState.AwaitingMethod, Hundred);

    private static PaymentSnapshot Snapshot(
        PaymentState state,
        AttemptOperation operation = AttemptOperation.Authorize,
        AttemptState? attemptState = null,
        int attemptCount = 0,
        int eventSeq = 0,
        long captured = 0,
        long refunded = 0,
        long pending = 0,
        bool liveCapture = false,
        bool liveVoid = false,
        RefundState? refundState = null,
        long refundAmount = 100)
    {
        var attempt = state is PaymentState.AwaitingMethod or PaymentState.Canceled
            ? null
            : new AttemptSnapshot(
                Attempt,
                operation,
                attemptState ?? DefaultAttemptState(state),
                Hundred,
                operation == AttemptOperation.Void ? Attempt : null,
                null);
        var refund = refundState is null
            ? null
            : new RefundSnapshot(Refund, refundState.Value, new Money(refundAmount, CurrencyCode.Usd));
        return new PaymentSnapshot(
            Payment,
            Merchant,
            state,
            Hundred,
            captured,
            refunded,
            pending,
            attemptCount,
            eventSeq,
            attempt,
            refund,
            liveCapture,
            liveVoid);
    }

    private static AttemptState DefaultAttemptState(PaymentState state) =>
        state switch
        {
            PaymentState.PspPending => AttemptState.Pending,
            PaymentState.ActionRequired => AttemptState.ActionRequired,
            PaymentState.Unknown => AttemptState.Unknown,
            PaymentState.Review => AttemptState.Review,
            _ => AttemptState.Succeeded,
        };

    private static PaymentCommand Psp(
        AttemptOperation operation,
        PspAnswerOutcome outcome,
        Money? answerAmount = null) =>
        new PaymentCommand.PspAnswer(Attempt, operation, outcome, answerAmount, null, null, Now);

    private static PaymentCommand Recovery(
        AttemptOperation operation,
        RecoveryOutcome outcome,
        Money? answerAmount = null) =>
        new PaymentCommand.RecoveryAnswer(Attempt, operation, outcome, answerAmount, null, null, Now);

    private static PaymentCommand Late(AttemptOperation operation) =>
        new PaymentCommand.LateApproval(Attempt, operation, null, null, Now);

    private static long NetAuthHold(IEnumerable<LedgerLine> ledger) =>
        ledger.Where(line => line.AccountType == Posting.AuthHold)
            .Sum(line => line.Direction == 'D' ? line.Amount.Minor : -line.Amount.Minor);

    private static IEnumerable<AttemptState> ResultAttemptStates(TransitionResult result) =>
        result is TransitionResult.Accepted { Attempt: not null } accepted
            ? [accepted.Attempt.State]
            : [];

    private sealed record GoldenCase(
        string Name,
        GoldenInitial Initial,
        GoldenCommand Command,
        PaymentState ExpectedState,
        IReadOnlyList<string> ExpectedLedger,
        IReadOnlyList<string> ExpectedEvents)
    {
        public PaymentSnapshot BuildSnapshot() =>
            new(
                Payment,
                Merchant,
                Initial.State,
                Hundred,
                Initial.CapturedMinor,
                Initial.RefundedMinor,
                Initial.RefundPendingMinor,
                Initial.AttemptCount,
                Initial.EventSeq,
                Initial.State is PaymentState.AwaitingMethod or PaymentState.Canceled
                    ? null
                    : new AttemptSnapshot(
                        Attempt,
                        Initial.Operation,
                        Initial.AttemptState,
                        Hundred,
                        Initial.Operation == AttemptOperation.Void ? Attempt : null,
                        null),
                Initial.RefundState is null
                    ? null
                    : new RefundSnapshot(Refund, Initial.RefundState.Value, new Money(Initial.RefundAmount, CurrencyCode.Usd)));

        public PaymentCommand BuildCommand() =>
            Command.Kind switch
            {
                "PspAnswer" => new PaymentCommand.PspAnswer(
                    Attempt,
                    Command.Operation,
                    Command.Outcome,
                    Command.AnswerAmount is null ? null : new Money(Command.AnswerAmount.Value, CurrencyCode.Usd),
                    Command.FailureCode,
                    Command.ActionType,
                    Now),
                "CaptureAnswer" => new PaymentCommand.CaptureAnswer(
                    CaptureAttempt,
                    Command.Approved,
                    Command.AnswerAmount is null ? null : new Money(Command.AnswerAmount.Value, CurrencyCode.Usd),
                    Command.FailureCode,
                    Command.TimedOut,
                    Now),
                "RefundConfirm" => new PaymentCommand.RefundConfirm(Refund, null, Now),
                _ => throw new InvalidOperationException($"Unknown golden command {Command.Kind}."),
            };
    }

    private sealed record GoldenInitial(
        PaymentState State,
        AttemptOperation Operation,
        AttemptState AttemptState,
        int AttemptCount,
        int EventSeq,
        long CapturedMinor,
        long RefundedMinor,
        long RefundPendingMinor,
        RefundState? RefundState,
        long RefundAmount);

    private sealed record GoldenCommand(
        string Kind,
        AttemptOperation Operation,
        PspAnswerOutcome Outcome,
        long? AnswerAmount,
        string? FailureCode,
        string? ActionType,
        bool Approved,
        bool TimedOut);
}
