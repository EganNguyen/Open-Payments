# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T041814Z-13e851a-s6-7017  
Date / commit / configuration: 2026-11-10T04:18:15Z / 13e851a / 6
Step and load profile: step 6, 1000 req/s, 900 s
Instances: API local, DB local, mock 1

Request shares: sale 40.0% authorize 10.5% capture 9.5% status 15.0% refund 10.0% retry 10.0% other 5.0%
Achieved: admitted req/s 959.44  rejected req/s 0.00  completed payments/s 485.46
Platform overhead latency: p50 33.08 ms  p95 772.24 ms  p99 1467.80 ms
End-to-end latency (PSP fixed at 200 ms): p50 233.08 ms  p95 972.24 ms  p99 1667.80 ms
Unexpected error rate: 0.000%

Resources: API CPU 237.75%, memory 475365376 bytes | DB CPU 2.66, I/O 1.5GB / 165GB | generator CPU 2.1%
Runtime: ThreadPool queue 5.0, Gen 2 GC/s 0.0, allocation rate 43493632 bytes/s
Database: pool wait 0.0 s, lock waits 4310.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 3545501 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 FAIL CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 9,562 ms |
| CK2 | pass | 1,786 ms |
| CK3 | pass | 67 ms |
| CK4 | pass | 819 ms |
| CK5 | FAIL | 66 ms |
| CK6 | pass | 21 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 661 ms |
| CK10 | pass | 501 ms |

Gates passed: no
Run valid: no
Invalid because: generator dropped 26761 arrivals
Gate reasons: p99 1467.80 ms is over 100 ms; p50 33.08 ms is over 25 ms; CK5 failed

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
