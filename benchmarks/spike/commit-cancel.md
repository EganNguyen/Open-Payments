# Commit Interruption Results

What a client sees when `COMMIT` is waiting for standbys and something interrupts it. This is the evidence behind [D28](../../docs/decisions.md#d28).

Status: Observed once, 2026-11-05 · Reproduce with `scripts/check-commit-cancel.sh`

This records behavior, not capacity, so running it on a laptop is fine. Environment: PostgreSQL 18.6, PgBouncer 1.24.0 in transaction mode, quorum `ANY 1 (standby1, standby2)`, Docker Desktop. Clients were `psql`, which cancels through the same protocol cancel request a driver uses. Npgsql itself isn't tested yet.

Standbys were stalled by pausing their containers. Each stalled scenario first confirmed a backend was waiting on `SyncRep`, so the fault was real.

## Results

Results were the same directly and through PgBouncer.

| Scenario | What the client saw | Row on the primary | Visible to others |
|---|---|---|---|
| One standby stalled | Normal `COMMIT` | Yes | Yes |
| Both stalled, then resumed, no interruption | Normal `COMMIT`, only after the resume | Yes | **No**, until a standby confirmed |
| Both stalled, client cancels | **`COMMIT` success**, with a `WARNING` that it is committed locally and may not have replicated | Yes | **Yes, while both standbys were still stalled** |
| Both stalled, backend terminated | `FATAL` and a lost connection, with a message that it is committed locally | Yes | Yes |

In the second scenario the commit was still waiting after 3 seconds, past the role's 1 second `statement_timeout`.

After the standbys resumed, the row reached them in every scenario.

## What this means

1. **A cancel can turn an unreplicated commit into a reported success.** The client gets a success, not an error. A failover before the standby catches up would lose a commit the client was told about, which breaks RPO 0. The loss itself wasn't forced here; the test stops at showing that only the primary has the row.
2. **`statement_timeout` doesn't limit the wait.** Without a cancel, a commit waits as long as both standbys are stalled. So "never cancel" needs a limit of its own, or a request can hang forever.
3. **A lost connection during commit doesn't mean the commit failed.** The row was there. A failed commit must be read as unknown.
4. **One stalled standby doesn't block commits,** which is what quorum replication is for ([D7](../../docs/decisions.md#d7)).

## Still to verify

- Whether Npgsql sends a cancel request when its own command timeout fires on a commit. Task 13 must test this, because if it does, a timeout causes the same problem as a cancel.
- An actual failover during this state. That belongs to C7 and C43 on the full stack.
