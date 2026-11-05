# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3061.2 | 6122.4 | 26932.447 | 53905.304 | 6.595 | 52.6 | 9.47 | 0.32 | 23.3 | 12.68 | 0 | True | ok | SyncRep 25%, ClientRead 22%, WALWrite 21%, BufferContent 14%, running 12% |

## Sustained commits/s by variant

- baseline: 0
