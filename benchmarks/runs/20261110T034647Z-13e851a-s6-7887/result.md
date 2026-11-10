# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T034647Z-13e851a-s6-7887  
Date / commit / configuration: 2026-11-10T03:46:47Z / 13e851a / 6
Step and load profile: step 6, 1000 req/s, 900 s
Instances: API local, DB local, mock 1

Request shares: sale 40.1% authorize 10.5% capture 9.5% status 15.0% refund 10.0% retry 10.0% other 5.0%
Achieved: admitted req/s 972.63  rejected req/s 0.00  completed payments/s 492.57
Platform overhead latency: p50 36.65 ms  p95 774.10 ms  p99 1083.76 ms
End-to-end latency (PSP fixed at 200 ms): p50 236.65 ms  p95 974.10 ms  p99 1283.76 ms
Unexpected error rate: 0.000%

Resources: API CPU 236.82%, memory 468611072 bytes | DB CPU 3.35, I/O 1.5GB / 142GB | generator CPU 2.3%
Runtime: ThreadPool queue 9.0, Gen 2 GC/s 0.0, allocation rate 44123872 bytes/s
Database: pool wait 0.0 s, lock waits 4554.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 3296507 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 13,156 ms |
| CK2 | pass | 1,836 ms |
| CK3 | pass | 62 ms |
| CK4 | pass | 806 ms |
| CK5 | pass | 49 ms |
| CK6 | pass | 27 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 701 ms |
| CK10 | pass | 434 ms |

Gates passed: no
Run valid: no
Invalid because: generator dropped 14037 arrivals
Gate reasons: p99 1083.76 ms is over 100 ms; p50 36.65 ms is over 25 ms

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
