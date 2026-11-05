# Spike Summary

**Local: comparative only, does not answer G1.**

Environment: macbook. Required commits/s for the target mix: 17499.
Gates: primary CPU at most 60%, the rate kept up with, verification passed.

| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | PgBouncer CPU % | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 500 | 500.3 | 1000.6 | 7.446 | 39.422 | 1.681 | 6.5 | 1.4 | 3.7 | 7.19 | 0 | True | ok |
| baseline | 1000 | 999.3 | 1998.7 | 10.952 | 49.846 | 2.99 | 11.0 | 2.4 | 5.7 | 5.09 | 0 | True | ok |
| baseline | 2000 | 1999.4 | 3998.9 | 59.229 | 454.61 | 6.427 | 25.3 | 5.5 | 14.8 | 9.25 | 0 | True | ok |
| baseline | 3000 | 2078.3 | 4156.5 | 17452.159 | 36265.107 | 7.787 | 26.9 | 5.8 | 19.7 | 9.04 | 0 | True | ok |
| baseline | 4000 | 2060.9 | 4121.7 | 25767.968 | 56958.962 | 8.442 | 32.2 | 5.6 | 19.2 | 8.69 | 0 | True | ok |
| baseline | 6000 | 2027.4 | 4054.8 | 36854.665 | 78153.14 | 7.891 | 37.9 | 6.4 | 18.1 | 8.47 | 0 | True | ok |
| baseline | 8000 | 2070.6 | 4141.2 | 43009.706 | 85127.542 | 9.433 | 42.1 | 5.1 | 18.0 | 8.64 | 0 | True | ok |

## Sustained commits/s by variant

- baseline: 3998.9
