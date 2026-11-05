# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 10000 | 2319.1 | 4638.1 | 33200.161 | 59821.254 | 9.572 | 55.3 | 9.95 | 0.32 | 13.8 | 9.58 | 0 | True | invalid | BufferContent 67%, ClientRead 15%, WALInsert 5%, running 4%, WALWrite 4% |

## Sustained commits/s by variant

- baseline: 0
