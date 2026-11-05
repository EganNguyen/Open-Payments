# Npgsql Commit Probe

How Npgsql behaves while `COMMIT` waits for standbys, and whether a commit can be abandoned without a cancel. This is the evidence for the 2026-11-05 amendment to [D28](../../docs/decisions.md#d28), answering [E1](../../docs/gates/stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28). It adds to the [first probe](../npgsql-commit-cancel.md).

Status: Run twice on 2026-11-05, same results · Reproduce with `benchmarks/npgsql-commit-probe/run.sh`

This records behavior, not capacity, so a laptop is fine. Environment: PostgreSQL 18.6, Npgsql 10.0.3, PgBouncer 1.26.1 in transaction mode, .NET SDK 10.0.403, on macOS. `synchronous_standby_names` names two standbys that don't exist, so a commit waits until the setting is cleared.

## Results

Results were the same directly and through PgBouncer, except for the error raised when a backend is terminated.

| Scenario | What the client saw | Cancel logged | Row visible |
|---|---|---|---|
| `Command Timeout=1` | **Normal success** at about 1 s, with a `WARNING 01000` notice | **Yes** | Yes |
| `Command Timeout=1`, `Cancellation Timeout=-1` | `NpgsqlException` from a `TimeoutException` at about 1 s | **Yes** | Yes |
| `Command Timeout=0`, stop waiting after 1 s with `WaitAsync` | Still pending at 4 s | No | No |
| The same, after the wait is released | Late success; connection reusable | No | Yes |
| Pool of 2, both held by waiting commits | Third open fails after its 1 s timeout | No | Not checked |
| The same, after the wait is released | Open succeeds | No | Not checked |
| `pg_cancel_backend` from outside | Normal success, with a `WARNING 01000` notice | Yes | Yes |
| `pg_terminate_backend` from outside | `NpgsqlException` directly, or `PostgresException 08P01` through PgBouncer; connection `Broken` | No, a termination warning instead | Yes |
| `psql` killed with `kill -9` during the wait | Client gone; the backend stays in `SyncRep` | No | No |

## What this means

1. **`Command Timeout` is as dangerous as a cancel.** It covers `COMMIT` and sends a cancel when it fires. Its default of 30 s should do the same, though only the 1 s case was run. The runner's connections must set it to 0.
2. **Setting `Cancellation Timeout` doesn't help.** With -1, the client gets an error instead of a false success, but the cancel is still sent.
3. **A commit can be abandoned safely by no longer waiting for it.** No cancel is sent, the row stays invisible, and the connection works again after the commit finishes.
4. **Waiting commits stay bounded.** They hold pool connections, so the pool sheds new work when it runs out.
5. **Dropping the connection would free nothing on the database.** The backend keeps waiting after its client goes away.
6. **A cancel from outside can be detected.** The `WARNING` notice reaches the client directly and through PgBouncer.

## Not tested

- Real standbys, stalled or otherwise. C43 covers that on the full stack.
- A dead network during the wait, which is why the runner must enable TCP keepalive.
- Npgsql's default 30 s `Command Timeout`.
- Whether a same-key retry during an abandoned commit gets a lock timeout. Task 13 tests it.
- PgBouncer 1.24.0, the version recorded in the [earlier server results](../spike/commit-cancel.md).
