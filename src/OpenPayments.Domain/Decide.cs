namespace OpenPayments.Domain;

/// <summary>
/// The pure payment rules (D22). It has no database, clock, random source or network access;
/// every identifier and time is supplied by the caller.
/// </summary>
public static class PaymentRules
{
    public static TransitionResult Decide(
        PaymentSnapshot current,
        PaymentCommand command,
        RuleVersion rules)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rules);

        if (current.IsFinal)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        return command switch
        {
            PaymentCommand.CreatePayment create => Create(current, create),
            PaymentCommand.Attempt attempt => BeginAttempt(current, attempt.AttemptId, AttemptOperation.Authorize, rules),
            PaymentCommand.Sale sale => BeginAttempt(current, sale.AttemptId, AttemptOperation.Sale, rules),
            PaymentCommand.FraudBlock block => FraudBlocked(current, block, rules),
            PaymentCommand.PspAnswer answer => PspAnswer(current, answer),
            PaymentCommand.RecoveryAnswer answer => RecoveryAnswer(current, answer),
            PaymentCommand.Timeout timeout => TimedOut(current, timeout),
            PaymentCommand.ActionCompleted completed => ActionCompleted(current, completed),
            PaymentCommand.Capture capture => BeginCapture(current, capture),
            PaymentCommand.CaptureAnswer answer => CaptureAnswer(current, answer),
            PaymentCommand.RefundReserve reserve => ReserveRefund(current, reserve),
            PaymentCommand.RefundConfirm confirm => ConfirmRefund(current, confirm),
            PaymentCommand.RefundRelease release => ReleaseRefund(current, release),
            PaymentCommand.RefundTimeout timeout => TimeoutRefund(current, timeout),
            PaymentCommand.RefundMismatch mismatch => MismatchRefund(current, mismatch),
            PaymentCommand.Void attempt => BeginVoid(current, attempt),
            PaymentCommand.VoidAnswer answer => VoidAnswer(current, answer),
            PaymentCommand.Expiry => Expire(current),
            PaymentCommand.Cancel => Cancel(current),
            PaymentCommand.ManualResolution resolution => Resolve(current, resolution),
            PaymentCommand.LateApproval approval => LateApproval(current, approval),
            _ => new TransitionResult.Rejected("unknown_command"),
        };
    }

    private static TransitionResult Create(PaymentSnapshot current, PaymentCommand.CreatePayment command)
    {
        if (current.State != PaymentState.AwaitingMethod || current.AttemptCount > 0)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (command.Amount.Minor <= 0)
        {
            return new TransitionResult.Rejected("invalid_amount");
        }

        if (!command.HasMethod)
        {
            return Accept(PaymentState.AwaitingMethod);
        }

        if (command.Operation is not (AttemptOperation.Authorize or AttemptOperation.Sale))
        {
            return new TransitionResult.Rejected("invalid_operation");
        }

        var attemptId = command.AttemptId
            ?? new AttemptId(Posting.Id(command.PaymentId.Value, "attempt:" + command.Operation));
        return Accept(
            PaymentState.PspPending,
            new AttemptChange(attemptId, command.Operation, AttemptState.Pending));
    }

    private static TransitionResult BeginAttempt(
        PaymentSnapshot current,
        AttemptId attemptId,
        AttemptOperation operation,
        RuleVersion rules)
    {
        if (current.State != PaymentState.AwaitingMethod)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.AttemptCount >= rules.AttemptCap)
        {
            return new TransitionResult.Rejected("attempt_cap_reached");
        }

        if (current.Attempt is { State: AttemptState.Pending or AttemptState.ActionRequired or AttemptState.Unknown })
        {
            return new TransitionResult.Rejected("attempt_live");
        }

        return Accept(
            PaymentState.PspPending,
            new AttemptChange(attemptId, operation, AttemptState.Pending));
    }

    private static TransitionResult FraudBlocked(
        PaymentSnapshot current,
        PaymentCommand.FraudBlock command,
        RuleVersion rules)
    {
        if (current.State != PaymentState.AwaitingMethod)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.AttemptCount >= rules.AttemptCap)
        {
            return new TransitionResult.Rejected("attempt_cap_reached");
        }

        return Accept(
            PaymentState.AwaitingMethod,
            new AttemptChange(command.AttemptId, AttemptOperation.Authorize, AttemptState.Failed, command.FailureCode),
            events: [Event(current, command.AttemptId.Value, "payment.attempt_failed")]);
    }

    private static TransitionResult PspAnswer(
        PaymentSnapshot current,
        PaymentCommand.PspAnswer command)
    {
        if (current.State is not (PaymentState.PspPending or PaymentState.ActionRequired or PaymentState.Unknown))
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (DoesNotMatch(command.AnswerAmount, current.Amount))
        {
            return Review(current, command.AttemptId, command.Operation);
        }

        return command.Outcome switch
        {
            PspAnswerOutcome.Approved when command.Operation == AttemptOperation.Sale =>
                CapturedSale(current, command.AttemptId),
            PspAnswerOutcome.Approved =>
                Authorized(current, command.AttemptId),
            PspAnswerOutcome.Declined =>
                FailedAttempt(current, command.AttemptId, command.Operation, command.FailureCode ?? "declined"),
            PspAnswerOutcome.RequiresAction =>
                RequiresAction(current, command.AttemptId, command.Operation),
            _ => new TransitionResult.Rejected("invalid_outcome"),
        };
    }

    private static TransitionResult RecoveryAnswer(
        PaymentSnapshot current,
        PaymentCommand.RecoveryAnswer command)
    {
        if (current.State is not (PaymentState.PspPending or PaymentState.Unknown))
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (command.Outcome == RecoveryOutcome.Inconclusive)
        {
            if (current.State == PaymentState.Unknown)
            {
                return new TransitionResult.NoOp("inconclusive");
            }

            return Accept(
                PaymentState.Unknown,
                new AttemptChange(command.AttemptId, command.Operation, AttemptState.Unknown));
        }

        if (command.Outcome is RecoveryOutcome.ProvenNotCharged or RecoveryOutcome.Declined)
        {
            var failureCode = command.FailureCode
                ?? (command.Outcome == RecoveryOutcome.ProvenNotCharged
                    ? "proven_not_charged"
                    : "declined");
            return FailedAttempt(current, command.AttemptId, command.Operation, failureCode);
        }

        if (DoesNotMatch(command.AnswerAmount, current.Amount))
        {
            return Review(current, command.AttemptId, command.Operation);
        }

        return command.Outcome switch
        {
            RecoveryOutcome.Approved when command.Operation == AttemptOperation.Sale =>
                CapturedSale(current, command.AttemptId),
            RecoveryOutcome.Approved =>
                Authorized(current, command.AttemptId),
            RecoveryOutcome.RequiresAction =>
                RequiresAction(current, command.AttemptId, command.Operation),
            _ => new TransitionResult.Rejected("invalid_outcome"),
        };
    }

    private static TransitionResult TimedOut(PaymentSnapshot current, PaymentCommand.Timeout command)
    {
        if (current.State == PaymentState.Unknown)
        {
            return new TransitionResult.NoOp("already_unknown");
        }

        if (current.State is not (PaymentState.PspPending or PaymentState.ActionRequired))
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        return Accept(
            PaymentState.Unknown,
            new AttemptChange(command.AttemptId, command.Operation, AttemptState.Unknown));
    }

    private static TransitionResult ActionCompleted(
        PaymentSnapshot current,
        PaymentCommand.ActionCompleted command)
    {
        if (current.State != PaymentState.ActionRequired)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        return Accept(
            PaymentState.PspPending,
            new AttemptChange(
                command.AttemptId,
                current.Attempt?.Operation ?? AttemptOperation.Authorize,
                AttemptState.Pending));
    }

    private static TransitionResult BeginCapture(
        PaymentSnapshot current,
        PaymentCommand.Capture command)
    {
        if (current.State != PaymentState.Authorized)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.LiveCapture)
        {
            return new TransitionResult.Rejected("capture_live");
        }

        if (current.LiveVoid)
        {
            return new TransitionResult.Rejected("void_live");
        }

        if (command.Amount != current.Amount)
        {
            return new TransitionResult.Rejected("invalid_amount");
        }

        return Accept(
            PaymentState.Authorized,
            new AttemptChange(command.CaptureAttemptId, AttemptOperation.Capture, AttemptState.Pending));
    }

    private static TransitionResult CaptureAnswer(
        PaymentSnapshot current,
        PaymentCommand.CaptureAnswer command)
    {
        if (current.State != PaymentState.Authorized)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (command.TimedOut)
        {
            return Accept(
                PaymentState.Authorized,
                new AttemptChange(command.CaptureAttemptId, AttemptOperation.Capture, AttemptState.Unknown));
        }

        if (!command.Approved)
        {
            return Accept(
                PaymentState.Authorized,
                new AttemptChange(
                    command.CaptureAttemptId,
                    AttemptOperation.Capture,
                    AttemptState.Failed,
                    command.FailureCode ?? "declined"),
                events: [Event(current, command.CaptureAttemptId.Value, "payment.capture_failed")]);
        }

        if (DoesNotMatch(command.AnswerAmount, current.Amount))
        {
            return Accept(
                PaymentState.Review,
                new AttemptChange(command.CaptureAttemptId, AttemptOperation.Capture, AttemptState.Review));
        }

        var ledger = new List<LedgerLine>();
        ledger.AddRange(Posting.HoldRelease(command.CaptureAttemptId, current.Amount, "capture"));
        ledger.AddRange(Posting.CaptureMoney(command.CaptureAttemptId, current.Amount));
        return Accept(
            PaymentState.Captured,
            new AttemptChange(command.CaptureAttemptId, AttemptOperation.Capture, AttemptState.Succeeded),
            ledger: ledger,
            events: [Event(current, command.CaptureAttemptId.Value, "payment.captured")]);
    }

    private static TransitionResult ReserveRefund(
        PaymentSnapshot current,
        PaymentCommand.RefundReserve command)
    {
        if (current.State is not (PaymentState.Captured or PaymentState.PartiallyRefunded))
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.Refund is { State: RefundState.Pending or RefundState.Unknown })
        {
            return new TransitionResult.Rejected("refund_live");
        }

        if (command.Amount.Currency != current.Amount.Currency)
        {
            return new TransitionResult.Rejected("currency_mismatch");
        }

        if (command.Amount.Minor <= 0 || command.Amount.Minor > current.RefundableMinor)
        {
            return new TransitionResult.Rejected("refund_exceeds_captured");
        }

        return Accept(
            current.State,
            refund: new RefundChange(command.RefundId, RefundState.Pending),
            ledger: Posting.RefundReservation(command.RefundId, command.Amount));
    }

    private static TransitionResult ConfirmRefund(
        PaymentSnapshot current,
        PaymentCommand.RefundConfirm command)
    {
        if (current.State is not (PaymentState.Captured or PaymentState.PartiallyRefunded)
            || current.Refund is null
            || current.Refund.Id != command.RefundId
            || current.Refund.State is not (RefundState.Pending or RefundState.Unknown))
        {
            return new TransitionResult.Rejected("refund_not_found");
        }

        var amount = current.Refund.Amount;
        if (DoesNotMatch(command.AnswerAmount, amount))
        {
            return Accept(
                current.State,
                refund: new RefundChange(command.RefundId, RefundState.Review));
        }

        var newState = current.RefundedMinor + amount.Minor == current.CapturedMinor
            ? PaymentState.Refunded
            : PaymentState.PartiallyRefunded;
        return Accept(
            newState,
            refund: new RefundChange(command.RefundId, RefundState.Succeeded),
            ledger: Posting.RefundConfirmation(command.RefundId, amount),
            events: [Event(current, command.RefundId.Value, "payment.refunded")]);
    }

    private static TransitionResult ReleaseRefund(
        PaymentSnapshot current,
        PaymentCommand.RefundRelease command)
    {
        if (current.State is not (PaymentState.Captured or PaymentState.PartiallyRefunded)
            || current.Refund is null
            || current.Refund.Id != command.RefundId
            || current.Refund.State is not (RefundState.Pending or RefundState.Unknown))
        {
            return new TransitionResult.Rejected("refund_not_found");
        }

        if (!command.ProvenNotCharged && command.FailureCode is null)
        {
            return new TransitionResult.NoOp("inconclusive");
        }

        return Accept(
            current.State,
            refund: new RefundChange(command.RefundId, RefundState.Failed, command.FailureCode),
            ledger: Posting.RefundRelease(command.RefundId, current.Refund.Amount),
            events: [Event(current, command.RefundId.Value, "payment.refund_failed")]);
    }

    private static TransitionResult TimeoutRefund(
        PaymentSnapshot current,
        PaymentCommand.RefundTimeout command)
    {
        if (current.Refund is null
            || current.Refund.Id != command.RefundId
            || current.Refund.State != RefundState.Pending)
        {
            return new TransitionResult.Rejected("refund_not_found");
        }

        return Accept(
            current.State,
            refund: new RefundChange(command.RefundId, RefundState.Unknown));
    }

    private static TransitionResult MismatchRefund(
        PaymentSnapshot current,
        PaymentCommand.RefundMismatch command)
    {
        if (current.Refund is null
            || current.Refund.Id != command.RefundId
            || current.Refund.State is not (RefundState.Pending or RefundState.Unknown))
        {
            return new TransitionResult.Rejected("refund_not_found");
        }

        return Accept(
            current.State,
            refund: new RefundChange(command.RefundId, RefundState.Review));
    }

    private static TransitionResult BeginVoid(
        PaymentSnapshot current,
        PaymentCommand.Void command)
    {
        if (current.State != PaymentState.Authorized)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.LiveCapture)
        {
            return new TransitionResult.Rejected("capture_live");
        }

        if (current.LiveVoid)
        {
            return new TransitionResult.Rejected("void_live");
        }

        return Accept(
            PaymentState.Authorized,
            new AttemptChange(
                command.VoidAttemptId,
                AttemptOperation.Void,
                AttemptState.Pending,
                VoidTargetAttemptId: command.TargetAttemptId));
    }

    private static TransitionResult VoidAnswer(
        PaymentSnapshot current,
        PaymentCommand.VoidAnswer command)
    {
        if (current.State == PaymentState.Review && command.Approved)
        {
            return Accept(
                PaymentState.AwaitingMethod,
                new AttemptChange(command.VoidAttemptId, AttemptOperation.Void, AttemptState.Succeeded));
        }

        if (current.State != PaymentState.Authorized)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (command.TimedOut)
        {
            return Accept(
                PaymentState.Authorized,
                new AttemptChange(command.VoidAttemptId, AttemptOperation.Void, AttemptState.Unknown));
        }

        if (!command.Approved)
        {
            return Accept(
                PaymentState.Authorized,
                new AttemptChange(
                    command.VoidAttemptId,
                    AttemptOperation.Void,
                    AttemptState.Failed,
                    command.FailureCode ?? "declined"),
                events: [Event(current, command.VoidAttemptId.Value, "payment.void_failed")]);
        }

        return Accept(
            PaymentState.Voided,
            new AttemptChange(command.VoidAttemptId, AttemptOperation.Void, AttemptState.Succeeded),
            ledger: Posting.HoldRelease(command.VoidAttemptId, current.Amount, "void"),
            events: [Event(current, command.VoidAttemptId.Value, "payment.voided")]);
    }

    private static TransitionResult Expire(PaymentSnapshot current)
    {
        if (current.State != PaymentState.Authorized)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        if (current.LiveCapture || current.LiveVoid)
        {
            return new TransitionResult.Rejected("operation_live");
        }

        if (current.Attempt is null)
        {
            return new TransitionResult.Rejected("attempt_missing");
        }

        return Accept(
            PaymentState.Expired,
            ledger: Posting.HoldRelease(current.Attempt.Id, current.Amount, "expiry"),
            events: [Event(current, current.Attempt.Id.Value, "payment.expired")]);
    }

    private static TransitionResult Cancel(PaymentSnapshot current)
    {
        if (current.State != PaymentState.AwaitingMethod)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        return Accept(
            PaymentState.Canceled,
            events: [Event(current, PaymentIdToGuid(current.Id), "payment.canceled")]);
    }

    private static TransitionResult Resolve(
        PaymentSnapshot current,
        PaymentCommand.ManualResolution command)
    {
        if (current.State != PaymentState.Review)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        var release = current.HoldPosted
            ? Posting.HoldRelease(CurrentAttempt(current), current.Amount, "manual")
            : [];
        return command.Resolution switch
        {
            PaymentResolution.Authorize => Accept(
                PaymentState.Authorized,
                ledger: current.HoldPosted
                    ? []
                    : Posting.AuthorizationHold(CurrentAttempt(current), current.Amount),
                events: [Event(current, CurrentAttempt(current).Value, "payment.authorized")]),
            PaymentResolution.AwaitingMethod => Accept(
                PaymentState.AwaitingMethod,
                ledger: release),
            PaymentResolution.Capture => Accept(
                PaymentState.Captured,
                ledger: release.Concat(Posting.CaptureMoney(CurrentAttempt(current), current.Amount)).ToList(),
                events: [Event(current, CurrentAttempt(current).Value, "payment.captured")]),
            _ => new TransitionResult.Rejected("invalid_resolution"),
        };
    }

    private static TransitionResult LateApproval(
        PaymentSnapshot current,
        PaymentCommand.LateApproval command)
    {
        if (current.State != PaymentState.AwaitingMethod)
        {
            return new TransitionResult.Rejected("invalid_state");
        }

        var voidTarget = command.Operation == AttemptOperation.Authorize
            ? command.AttemptId
            : (AttemptId?)null;
        return Accept(
            PaymentState.Review,
            new AttemptChange(
                command.AttemptId,
                command.Operation,
                AttemptState.Review,
                command.FailureCode,
                voidTarget));
    }

    private static TransitionResult Authorized(PaymentSnapshot current, AttemptId attemptId) =>
        Accept(
            PaymentState.Authorized,
            new AttemptChange(attemptId, AttemptOperation.Authorize, AttemptState.Succeeded),
            ledger: Posting.AuthorizationHold(attemptId, current.Amount),
            events: [Event(current, attemptId.Value, "payment.authorized")]);

    private static TransitionResult CapturedSale(PaymentSnapshot current, AttemptId attemptId) =>
        Accept(
            PaymentState.Captured,
            new AttemptChange(attemptId, AttemptOperation.Sale, AttemptState.Succeeded),
            ledger: Posting.CaptureMoney(attemptId, current.Amount),
            events: [Event(current, attemptId.Value, "payment.captured")]);

    private static TransitionResult FailedAttempt(
        PaymentSnapshot current,
        AttemptId attemptId,
        AttemptOperation operation,
        string failureCode) =>
        Accept(
            PaymentState.AwaitingMethod,
            new AttemptChange(attemptId, operation, AttemptState.Failed, failureCode),
            events: [Event(current, attemptId.Value, "payment.attempt_failed")]);

    private static TransitionResult RequiresAction(
        PaymentSnapshot current,
        AttemptId attemptId,
        AttemptOperation operation) =>
        Accept(
            PaymentState.ActionRequired,
            new AttemptChange(attemptId, operation, AttemptState.ActionRequired),
            events: [Event(current, attemptId.Value, "payment.action_required")]);

    private static TransitionResult Review(
        PaymentSnapshot current,
        AttemptId attemptId,
        AttemptOperation operation) =>
        Accept(
            PaymentState.Review,
            new AttemptChange(attemptId, operation, AttemptState.Review));

    private static TransitionResult.Accepted Accept(
        PaymentState state,
        AttemptChange? attempt = null,
        RefundChange? refund = null,
        IReadOnlyList<LedgerLine>? ledger = null,
        IReadOnlyList<OutboxEvent>? events = null) =>
        new(state, attempt, refund, ledger ?? [], events ?? []);

    private static bool DoesNotMatch(Money? answer, Money expected) =>
        answer is not null && answer != expected;

    private static OutboxEvent Event(PaymentSnapshot current, Guid baseId, string eventType) =>
        Posting.Event(baseId, eventType, current.EventSeq + 1, current.Id.Value);

    private static AttemptId CurrentAttempt(PaymentSnapshot current) =>
        current.Attempt?.Id ?? new AttemptId(PaymentIdToGuid(current.Id));

    private static Guid PaymentIdToGuid(PaymentId payment) => payment.Value;
}
