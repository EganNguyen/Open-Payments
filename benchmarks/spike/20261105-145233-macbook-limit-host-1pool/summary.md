# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2018.3 | 4036.5 | 30425.261 | 62410.921 | 6.512 | 24.4 | 4.4 | 0.97 | 16.2 | 21.91 | 0 | True | ok | ClientRead 85%, SyncRep 7%, running 3%, WALWrite 3%, WalSync 0% |

## Sustained commits/s by variant

- baseline: 0
