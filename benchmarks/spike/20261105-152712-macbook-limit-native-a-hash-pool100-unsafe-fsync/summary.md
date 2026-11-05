# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2027.4 | 4054.8 | 30150.809 | 61637.393 | 19.812 | 12.6 | 2.28 | 0.14 | 30.6 | 8.32 | 0 | True | invalid | SyncRep 48%, WALWrite 31%, ClientRead 7%, BufferContent 5%, running 4% |

## Sustained commits/s by variant

- baseline: 0
