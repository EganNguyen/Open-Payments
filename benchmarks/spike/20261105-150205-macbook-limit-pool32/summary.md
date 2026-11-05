# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 1856.5 | 3712.9 | 33510.449 | 63085.748 | 3.886 | 46.0 | 8.29 | 0.23 | 19.0 | 7.82 | 0 | True | ok | running 29%, ClientRead 27%, SyncRep 23%, WALWrite 9%, WALInsert 5% |

## Sustained commits/s by variant

- baseline: 0
