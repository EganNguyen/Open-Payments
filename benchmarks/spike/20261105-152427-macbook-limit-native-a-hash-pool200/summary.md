# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2781.8 | 5563.7 | 28809.652 | 56003.155 | 28.729 | 35.4 | 6.37 | 0.2 | 36.6 | 11.57 | 0 | True | invalid | SyncRep 41%, WALWrite 29%, ClientRead 15%, running 7%, BufferContent 5% |

## Sustained commits/s by variant

- baseline: 0
