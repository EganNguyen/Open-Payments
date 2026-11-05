# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2013.2 | 4026.5 | 29979.386 | 61953.271 | 12.985 | 68.1 | 12.25 | 0.01 | 20.1 | 8.54 | 0 | True | invalid | BufferContent 88%, ClientRead 3%, running 3%, WALInsert 3%, SyncRep 2% |

## Sustained commits/s by variant

- baseline: 0
