# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3641.7 | 7283.5 | 24044.518 | 50706.346 | 69.776 | 58.4 | 10.52 | 0.24 | 27.9 | 15.41 | 0 | True | invalid | SyncRep 72%, WALWrite 11%, ClientRead 8%, running 3%, MultiXactGen 3% |

## Sustained commits/s by variant

- baseline: 0
