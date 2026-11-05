# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 1000 | 993.7 | 1987.3 | 48.824 | 170.372 | 25.241 | 6.9 | 1.25 | 0.08 | 4.9 | 4.13 | 0 | True | ok | WALWrite 48%, SyncRep 43%, DataFileExtend 3%, running 2%, WalSync 2% |
| baseline | 1500 | 1497.5 | 2995.0 | 48.535 | 194.756 | 27.022 | 8.5 | 1.54 | 0.1 | 6.9 | 6.01 | 0 | True | ok | WALWrite 48%, SyncRep 43%, MultiXactGen 2%, running 2%, ClientRead 2% |
| baseline | 2000 | 1992.6 | 3985.2 | 60.523 | 337.597 | 30.941 | 13.3 | 2.39 | 0.13 | 11.8 | 8.05 | 0 | True | invalid | SyncRep 42%, WALWrite 38%, MultiXactGen 10%, running 2%, ClientRead 2% |
| baseline | 2500 | 2367.2 | 4734.5 | 3017.487 | 6597.253 | 34.865 | 17.7 | 3.19 | 0.16 | 27.0 | 9.59 | 0 | True | invalid | SyncRep 47%, WALWrite 38%, MultiXactGen 5%, ClientRead 4%, running 2% |
| baseline | 3000 | 2512.4 | 5024.7 | 5427.525 | 19009.245 | 33.945 | 20.8 | 3.75 | 0.17 | 27.9 | 10.21 | 0 | True | ok | WALWrite 52%, SyncRep 38%, ClientRead 4%, running 2%, MultiXactGen 2% |
| baseline | 3500 | 2283.6 | 4567.2 | 13247.129 | 39965.603 | 38.543 | 17.5 | 3.16 | 0.16 | 30.2 | 9.24 | 0 | True | ok | SyncRep 49%, WALWrite 41%, ClientRead 4%, MultiXactGen 2%, running 2% |
| baseline | 4000 | 2826.0 | 5652.1 | 15102.141 | 35099.131 | 30.715 | 23.4 | 4.21 | 0.19 | 36.1 | 11.5 | 0 | True | invalid | WALWrite 51%, SyncRep 33%, ClientRead 8%, running 3%, MultiXactGen 3% |

## Sustained commits/s by variant

- baseline: 2995.0
