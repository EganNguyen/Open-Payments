# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 1000 | 996.2 | 1992.3 | 44.617 | 118.628 | 21.008 | 5.3 | 0.95 | 0.07 | 4.9 | 4.01 | 0 | True | invalid | SyncRep 61%, WALWrite 31%, BufferContent 3%, WalSync 2%, DataFileExtend 1% |
| baseline | 1500 | 1510.1 | 3020.3 | 45.165 | 127.694 | 21.627 | 8.3 | 1.49 | 0.11 | 6.7 | 6.12 | 0 | True | invalid | SyncRep 54%, WALWrite 40%, ClientRead 2%, WalSync 1%, running 1% |
| baseline | 2000 | 1991.0 | 3981.9 | 54.366 | 218.979 | 25.016 | 12.5 | 2.26 | 0.13 | 9.5 | 8.42 | 0 | True | invalid | SyncRep 43%, WALWrite 33%, running 7%, ClientRead 5%, MultiXactGen 4% |
| baseline | 2500 | 2499.3 | 4998.7 | 57.855 | 352.763 | 23.898 | 17.5 | 3.15 | 0.17 | 14.2 | 10.37 | 0 | True | invalid | SyncRep 42%, WALWrite 35%, running 8%, BufferContent 6%, ClientRead 3% |
| baseline | 3000 | 2580.1 | 5160.1 | 1159.684 | 20631.073 | 25.307 | 21.1 | 3.79 | 0.2 | 19.5 | 10.66 | 0 | True | invalid | WALWrite 41%, SyncRep 36%, BufferContent 9%, running 5%, MultiXactGen 4% |

## Sustained commits/s by variant

- baseline: 0
