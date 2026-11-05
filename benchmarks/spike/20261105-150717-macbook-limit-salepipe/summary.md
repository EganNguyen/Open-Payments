# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 4505.5 | 9011.0 | 18854.807 | 42334.786 | n/a | 61.0 | 10.97 | 0.15 | 12.1 | 17.98 | 0 | True | ok | SyncRep 51%, BufferContent 21%, WALWrite 13%, running 11%, WALInsert 3% |

## Sustained commits/s by variant

- baseline: 0
