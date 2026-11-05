# Stage 1 Escalations

An entry here stops work on the named task until a design session answers it. The rules are in [Delivery workflow](../delivery-workflow.md#build-gate).

## E1: Task 13 Npgsql commit cancellation breaks D28

**State:** Answered 2026-11-05, Task 13 may resume · Raised 2026-11-05

**Task:** 13, transaction runner and store. The runner cannot implement the `COMMIT` behavior D28 requires with Npgsql 10.0.3's public API.

**What conflicts:** D28 says no cancel is ever sent during `COMMIT`; after the 3 s wait limit the connection is dropped, and a dropped connection or a server "canceling wait" warning makes the outcome unknown. Task 13 says to check Npgsql's timeout behavior first and stop if its timeout sends a cancel and closing the socket can't avoid it.

The probe found both halves of the stop condition:

- `NpgsqlTransaction.CommitAsync(CancellationToken)` with a 1 s token sent `canceling wait for synchronous replication due to user request`, and the client received a normal commit success. The row was committed locally but not replicated.
- `NpgsqlConnection.CloseAsync()`/`DisposeAsync()` during the commit threw `NpgsqlOperationInProgressException: The connection is already in state 'Executing'`. The commit stayed pending and the connection could not be dropped through the public API.
- `statement_timeout` and `CommandTimeout` do not cover the synchronous commit wait.

**Evidence:** [Npgsql commit cancellation probe](../benchmarks/npgsql-commit-cancel.md), from PostgreSQL 18.6 + Npgsql 10.0.3, with the server log lines and the two probe outputs. Reproduce by starting PostgreSQL with `synchronous_standby_names = 'ANY 1 (standby1, standby2)'` and no standby, then committing with and without a token as recorded. The earlier server observation is [commit-cancel.md](../benchmarks/spike/commit-cancel.md).

**Options seen:**

1. Provide a supported way to drop the PostgreSQL socket at the 3 s limit without a cancel, for example a driver API, a custom connection wrapper, or a connection proxy. This keeps D28 and RPO 0, and needs a design decision on the mechanism.
2. Change the driver or the commit protocol to one that can abandon the wait without a cancel. This is a design change with an owner decision.
3. Accept the cancellation and treat the server warning as unknown, with the client receiving 202 rather than the driver's success. This still sends a cancel, so it conflicts with D28's first sentence and with RPO 0; it needs an explicit owner amendment, not a builder choice.
4. Give up synchronous commit or RPO 0. This is a gate/owner decision and conflicts with the design's stated durability.

**Recommendation:** Option 1 if Npgsql or a small wrapper can drop the socket without a cancel; otherwise send the measured behavior to the owner and amend D28 before building the runner. Do not take option 3 silently.

**Impact:** Tasks 13-17 depend on the runner and store. Task 12 (pure `Decide`) is complete and committed. No Task 13 code was committed.

**Answer (design session, D28 amendment approved by the owner, 2026-11-05):** a commit can be abandoned without a cancel, by no longer waiting for it. None of the four options is needed. [D28](../decisions.md#d28) is amended and the [probe](../../benchmarks/npgsql-commit-probe/README.md) is the evidence. It also corrects the entry above: `Command Timeout` does cover `COMMIT`, and it sends a cancel.

**Required in `TxRunner`:**

1. Use a dedicated data source with `Command Timeout=0`, and fail at startup if it's anything else. Work statements stay limited by `ct` and the role's `statement_timeout`.
2. Enable TCP keepalive on that data source, so a dead peer can't hold a connection forever.
3. Call `CommitAsync(CancellationToken.None)` explicitly, then `WaitAsync(limit)` with no request or host token. Never call `NpgsqlCommand.Cancel`.
4. On timeout, throw `CommitOutcomeUnknownException` and hand the connection and transaction to a continuation. Don't dispose them while the commit runs, because dispose throws during `Executing`.
5. The continuation observes the late result, logs it, updates metrics, and only then disposes the connection. No exception goes unobserved.
6. Treat any notice with `InvariantSeverity` `WARNING` during `COMMIT` as unknown, count it and raise the page signal. Don't match on message text. Attach the handler only for the commit.
7. Treat any exception from `COMMIT` as unknown, not failure.
8. Keep waiting commits inside the bounded pool, with no second pool or queue. An exhausted pool sheds new work as Busy.
9. Add a gauge for abandoned commits still running and a counter for commit warnings to [Metrics](../metrics.md).
10. At shutdown, don't dispose connections with a commit still running.

**Configuration:** PgBouncer `query_timeout` stays 0 and Postgres `transaction_timeout` stays unset, because either could interrupt the wait. Add both to `scripts/check-db-settings.sh`.

**Tests:** the four added to [Task 13](../superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md#task-13-transaction-runner-and-store). Run the C43 tests directly and through PgBouncer.

**Escalate again if** a test shows a cancel during `COMMIT`, a success reported while no standby has confirmed and no warning arrived, an abandoned connection that isn't released after a standby returns, or a same-key retry during an abandoned commit that doesn't return Busy.
