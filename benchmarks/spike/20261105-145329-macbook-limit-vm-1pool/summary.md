# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 3318.6 | 6637.2 | 26474.474 | 52327.42 | 4.497 | 24.9 | 4.49 | 0.99 | 18.2 | 15.81 | 0 | True | ok | ClientRead 80%, SyncRep 10%, WALWrite 5%, running 3%, WalSync 1% |

## Sustained commits/s by variant

- baseline: 0
