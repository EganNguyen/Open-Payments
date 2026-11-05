# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3655.0 | 7309.9 | 23916.969 | 48946.082 | 7.068 | 44.8 | 8.07 | 0.34 | 25.6 | 15.39 | 0 | True | ok | BufferContent 75%, ClientRead 8%, WALInsert 4%, SyncRep 4%, WALWrite 3% |

## Sustained commits/s by variant

- baseline: 0
