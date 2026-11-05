# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | PgBouncer CPU % | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 500 | 501.4 | 1002.8 | 8.282 | 33.206 | 1.993 | 4.6 | 1.5 | 3.7 | 2.28 | 0 | True | ok |
| baseline | 1000 | 1006.8 | 2013.5 | 14.778 | 57.087 | 4.817 | 11.3 | 3.0 | 6.1 | 4.09 | 0 | True | ok |
| baseline | 2000 | 1972.0 | 3944.0 | 64.273 | 861.024 | 8.409 | 24.1 | 6.1 | 15.0 | 8.05 | 0 | True | ok |
| baseline | 3000 | 2137.7 | 4275.5 | 15134.659 | 33173.513 | 7.771 | 29.9 | 6.6 | 19.3 | 8.81 | 0 | True | ok |
| baseline | 4000 | 1895.3 | 3790.6 | 29399.602 | 62351.808 | 10.464 | 30.2 | 4.2 | 17.4 | 9.43 | 0 | True | ok |
| baseline | 6000 | 1872.4 | 3744.8 | 38090.978 | 80934.189 | 11.226 | 20.3 | 6.2 | 17.0 | 10.46 | 0 | True | ok |
| baseline | 8000 | 1827.4 | 3654.9 | 42492.679 | 91086.218 | 10.732 | 37.6 | 5.2 | 15.0 | 8.98 | 0 | True | ok |

## Sustained commits/s by variant

- baseline: 3944.0
