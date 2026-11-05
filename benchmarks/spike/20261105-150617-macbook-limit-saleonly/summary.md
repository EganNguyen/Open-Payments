# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3636.0 | 7272.0 | 23454.918 | 49269.372 | 4.966 | 48.2 | 8.68 | 0.36 | 24.9 | 14.5 | 0 | True | ok | BufferContent 37%, ClientRead 18%, WALWrite 15%, running 11%, SyncRep 9% |

## Sustained commits/s by variant

- baseline: 0
