# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 1028.9 | 2057.7 | 35416.028 | 69795.398 | 20.708 | 7.7 | 1.39 | 0.07 | 18.7 | 4.29 | 0 | True | ok | SyncRep 51%, WALWrite 38%, ClientRead 4%, WalSync 2%, running 2% |

## Sustained commits/s by variant

- baseline: 0
