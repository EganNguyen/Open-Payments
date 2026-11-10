# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T040232Z-13e851a-s6-1e11  
Date / commit / configuration: 2026-11-10T04:02:32Z / 13e851a / 6
Step and load profile: step 6, 1000 req/s, 900 s
Instances: API local, DB local, mock 1

Request shares: sale 40.0% authorize 10.5% capture 9.5% status 15.0% refund 10.0% retry 10.0% other 5.0%
Achieved: admitted req/s 943.39  rejected req/s 0.00  completed payments/s 477.09
Platform overhead latency: p50 64.25 ms  p95 918.87 ms  p99 1771.44 ms
End-to-end latency (PSP fixed at 200 ms): p50 264.25 ms  p95 1118.87 ms  p99 1971.44 ms
Unexpected error rate: 0.000%

Resources: API CPU 231.32%, memory 463437824 bytes | DB CPU 2.98, I/O 1.5GB / 153GB | generator CPU 2.1%
Runtime: ThreadPool queue 22.0, Gen 2 GC/s 0.0, allocation rate 43515087 bytes/s
Database: pool wait 1.0 s, lock waits 4935.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 3500262 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 11,386 ms |
| CK2 | pass | 2,043 ms |
| CK3 | pass | 63 ms |
| CK4 | pass | 804 ms |
| CK5 | pass | 56 ms |
| CK6 | pass | 25 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 718 ms |
| CK10 | pass | 559 ms |

Gates passed: no
Run valid: no
Invalid because: generator dropped 40889 arrivals
Gate reasons: p99 1771.44 ms is over 100 ms; p50 64.25 ms is over 25 ms

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
