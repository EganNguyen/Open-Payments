# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 1819.3 | 3638.6 | 33939.771 | 63188.442 | 19.785 | 71.3 | 12.83 | 0.02 | 18.4 | 7.73 | 0 | True | invalid | BufferContent 83%, SyncRep 8%, WALInsert 3%, running 3%, ClientRead 2% |

## Sustained commits/s by variant

- baseline: 0
