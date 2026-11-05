# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 4866.4 | 9732.8 | 20269.136 | 39583.787 | 4.778 | 42.5 | 7.65 | 0.48 | 29.5 | 19.96 | 0 | True | ok | ClientRead 41%, SyncRep 21%, running 11%, BufferContent 11%, WALWrite 8% |

## Sustained commits/s by variant

- baseline: 0
