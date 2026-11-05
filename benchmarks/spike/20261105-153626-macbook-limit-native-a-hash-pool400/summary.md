# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 1000 | 1001.0 | 2002.0 | 45.533 | 118.422 | 21.614 | 5.6 | 1.0 | 0.08 | 5.1 | 4.39 | 0 | True | invalid | SyncRep 49%, WALWrite 39%, running 3%, WalSync 2%, ClientRead 2% |
| baseline | 1500 | 1495.4 | 2990.7 | 46.324 | 134.095 | 22.114 | 8.2 | 1.48 | 0.11 | 6.6 | 6.07 | 0 | True | ok | SyncRep 59%, WALWrite 29%, running 2%, ClientRead 2%, BufferContent 2% |
| baseline | 2000 | 1981.7 | 3963.4 | 52.899 | 203.731 | 25.061 | 12.0 | 2.17 | 0.13 | 9.2 | 8.06 | 0 | True | invalid | WALWrite 45%, SyncRep 34%, ClientRead 7%, running 3%, BufferContent 3% |
| baseline | 2500 | 2258.8 | 4517.6 | 104.216 | 20113.555 | 26.754 | 16.5 | 2.97 | 0.17 | 12.3 | 9.23 | 0 | True | invalid | WALWrite 40%, SyncRep 39%, BufferContent 8%, running 5%, ClientRead 3% |
| baseline | 3000 | 1751.3 | 3502.7 | 1527.895 | 63586.238 | 23.298 | 13.1 | 2.35 | 0.14 | 9.5 | 7.16 | 0 | True | invalid | WALWrite 44%, SyncRep 27%, BufferContent 12%, ClientRead 10%, running 4% |

## Sustained commits/s by variant

- baseline: 2990.7
