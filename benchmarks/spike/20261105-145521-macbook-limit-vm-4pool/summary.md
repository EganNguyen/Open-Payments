# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 4879.6 | 9759.1 | 19506.651 | 39458.133 | 4.388 | 46.7 | 8.41 | 0.45 | 29.4 | 20.82 | 0 | True | ok | BufferContent 27%, ClientRead 26%, WALWrite 18%, WALInsert 10%, running 10% |

## Sustained commits/s by variant

- baseline: 0
