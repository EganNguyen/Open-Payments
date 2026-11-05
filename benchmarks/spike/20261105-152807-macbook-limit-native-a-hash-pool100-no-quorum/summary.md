# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2488.8 | 4977.7 | 29631.827 | 57900.85 | 13.751 | 15.8 | 2.85 | 0.17 | 29.2 | 10.22 | 0 | True | invalid | WALWrite 67%, ClientRead 15%, running 9%, BufferContent 7%, DataFileExtend 2% |

## Sustained commits/s by variant

- baseline: 0
