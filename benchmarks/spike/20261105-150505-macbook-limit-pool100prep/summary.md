# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2646.3 | 5292.6 | 44042.89 | 59878.129 | 4.206 | 27.0 | 4.85 | 0.3 | 9.5 | 10.57 | 0 | True | invalid | ClientRead 57%, BufferContent 22%, running 6%, SyncRep 5%, WALInsert 4% |

## Sustained commits/s by variant

- baseline: 0
