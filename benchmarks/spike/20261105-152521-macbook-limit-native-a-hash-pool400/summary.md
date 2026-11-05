# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3794.4 | 7588.8 | 25603.673 | 46896.015 | 37.415 | 59.7 | 10.74 | 0.26 | 29.3 | 15.98 | 0 | True | invalid | SyncRep 47%, WALWrite 30%, ClientRead 13%, BufferContent 5%, running 3% |

## Sustained commits/s by variant

- baseline: 0
