# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 1000 | 998.1 | 1996.2 | 45.295 | 117.934 | 21.485 | 5.6 | 1.02 | 0.08 | 5.1 | 4.44 | 0 | True | ok | SyncRep 47%, WALWrite 46%, running 3%, WalSync 2%, DataFileExtend 1% |
| baseline | 1500 | 1493.9 | 2987.8 | 48.562 | 182.482 | 23.699 | 8.1 | 1.45 | 0.1 | 6.6 | 8.33 | 0 | True | invalid | SyncRep 50%, WALWrite 39%, running 3%, ClientRead 2%, MultiXactGen 1% |
| baseline | 2000 | 1989.2 | 3978.4 | 51.849 | 1826.336 | 25.352 | 12.8 | 2.31 | 0.14 | 10.7 | 13.68 | 0 | True | invalid | WALWrite 46%, SyncRep 31%, ClientRead 7%, running 6%, BufferContent 6% |
| baseline | 2500 | 2362.9 | 4725.8 | 59.089 | 4888.864 | 25.011 | 16.1 | 2.89 | 0.16 | 13.3 | 14.6 | 0 | True | invalid | WALWrite 43%, SyncRep 35%, BufferContent 8%, ClientRead 5%, running 4% |
| baseline | 3000 | 2985.8 | 5971.6 | 123.095 | 1826.146 | 25.128 | 22.2 | 3.99 | 0.21 | 26.2 | 13.91 | 0 | True | invalid | WALWrite 40%, SyncRep 40%, running 6%, ClientRead 6%, BufferContent 5% |

## Sustained commits/s by variant

- baseline: 1996.2
