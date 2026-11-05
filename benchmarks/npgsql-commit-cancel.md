# Npgsql Commit Cancellation Probe

Task 13 requires the runner to check how Npgsql behaves before implementing D28. This probe ran on 2026-11-05 against PostgreSQL 18.6 with synchronous standbys configured but absent, so every commit waited for a standby that could not acknowledge it.

## Environment

| Item | Value |
|---|---|
| Machine | MacBook Pro, Apple M5 Pro, macOS |
| .NET | SDK 10.0.403 |
| Driver | Npgsql 10.0.3 |
| Server | PostgreSQL 18.6 in Docker, port 25433 |
| Server settings | `synchronous_standby_names = 'ANY 1 (standby1, standby2)'`, `synchronous_commit = on` |
| Standbys | None connected, so a synchronous commit waits indefinitely |
| Table | `commit_probe(id int primary key)` |
| Session | `set synchronous_commit = on` before the transaction |

The probe inserted one row in a transaction and then committed. It ran twice: once without a cancellation token, and once with a token that fired after 1 second.

## Result 1: no token, then close the connection

```
after 1.5s: completed=False elapsed=1501ms
CloseAsync threw NpgsqlOperationInProgressException: The connection is already in state 'Executing'
commit completed later=False elapsed=6505ms
rows after scenario: 0
```

`NpgsqlConnection.CloseAsync()` and `DisposeAsync()` cannot drop the socket while the commit is executing; they throw `NpgsqlOperationInProgressException`. The commit was still pending after 6.5 seconds and the row was not visible. The backend was cleared with `pg_terminate_backend` during cleanup, and the server logged the same D28 warning when that happened:

```
WARNING:  canceling the wait for synchronous replication and terminating connection due to administrator command
DETAIL:  The transaction has already committed locally, but might not have been replicated to the standby.
```

## Result 2: `CommitAsync` with a 1-second cancellation token

```
after 1.5s: completed=True elapsed=1009ms
commit result: success
rows after scenario: 1
```

The client was told the commit succeeded. The server log shows that Npgsql sent a cancel:

```
2026-11-05 10:55:16.621 UTC [112] WARNING:  canceling wait for synchronous replication due to user request
2026-11-05 10:55:16.621 UTC [112] DETAIL:  The transaction has already committed locally, but might not have been replicated to the standby.
```

This is the exact failure D28 was written to prevent: a normal success is returned while no standby has the row, so a failover could lose it.

## Conclusion

With Npgsql 10.0.3 and the public API:

- Passing a cancellation token to `NpgsqlTransaction.CommitAsync` sends a cancel to PostgreSQL and reports success even though the synchronous wait was cancelled. This violates D28 and RPO 0.
- `NpgsqlConnection.CloseAsync`/`DisposeAsync` cannot drop the connection during the commit. There is no supported way to abandon the commit without sending a cancel.
- `statement_timeout` and `CommandTimeout` do not cover the synchronous commit wait.

This is the escalation trigger in Task 13. The runner must not choose a workaround itself; see `docs/gates/stage-1-escalations.md`.
