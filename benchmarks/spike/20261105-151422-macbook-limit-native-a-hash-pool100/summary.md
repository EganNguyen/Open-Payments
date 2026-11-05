# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 1615.7 | 3231.5 | 33104.75 | 64834.981 | 26.618 | 15.5 | 2.78 | 0.12 | 27.0 | 6.93 | 0 | True | invalid | WALWrite 54%, SyncRep 34%, ClientRead 4%, running 4%, BufferContent 2% |

## Sustained commits/s by variant

- baseline: 0
