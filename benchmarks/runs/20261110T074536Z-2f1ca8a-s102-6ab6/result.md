# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T074536Z-2f1ca8a-s102-6ab6  
Date / commit / configuration: 2026-11-10T07:45:36Z / 2f1ca8a / 102
Step and load profile: step 102, 250 req/s, 3600 s
Instances: API local, DB local, mock 1

Request shares: sale 40.0% authorize 10.3% capture 9.7% status 15.0% refund 10.0% retry 10.0% other 5.0%
Achieved: admitted req/s 249.33  rejected req/s 0.00  completed payments/s 125.43
Platform overhead latency: p50 7.70 ms  p95 14.91 ms  p99 28.60 ms
End-to-end latency (PSP fixed at 200 ms): p50 207.70 ms  p95 214.91 ms  p99 228.60 ms
Unexpected error rate: 0.000%

Resources: API CPU 88.41%, memory 207773696 bytes | DB CPU 29.70, I/O 39.1MB / 57.3GB | generator CPU 0.7%
Runtime: ThreadPool queue 2.0, Gen 2 GC/s 0.0, allocation rate 23187409 bytes/s
Database: pool wait 0.0 s, lock waits 1230.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 912201 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 12,520 ms |
| CK2 | pass | 1,998 ms |
| CK3 | pass | 74 ms |
| CK4 | pass | 938 ms |
| CK5 | pass | 82 ms |
| CK6 | pass | 28 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 592 ms |
| CK10 | pass | 586 ms |

Gates passed: yes
Run valid: no
Invalid because: generator dropped 22 arrivals

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
