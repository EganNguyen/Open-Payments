# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3022.3 | 6044.5 | 26353.249 | 53093.999 | 4.491 | 31.8 | 5.72 | 0.9 | 21.0 | 12.78 | 0 | True | ok | ClientRead 81%, running 7%, SyncRep 6%, WALWrite 4%, BufferContent 1% |

## Sustained commits/s by variant

- baseline: 0
