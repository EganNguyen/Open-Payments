# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3159.9 | 6319.8 | 27176.369 | 52725.331 | 4.214 | 47.2 | 8.49 | 0.33 | 24.0 | 13.06 | 0 | True | ok | BufferContent 24%, ClientRead 22%, SyncRep 19%, running 16%, WALInsert 11% |

## Sustained commits/s by variant

- baseline: 0
