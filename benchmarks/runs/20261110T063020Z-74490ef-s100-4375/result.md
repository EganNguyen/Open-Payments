# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T063020Z-74490ef-s100-4375  
Date / commit / configuration: 2026-11-10T06:30:20Z / 74490ef / 100
Step and load profile: step 100, 500 req/s, 3600 s
Instances: API local, DB local, mock 1

Request shares: sale 40.1% authorize 10.2% capture 9.7% status 15.0% refund 10.0% retry 10.0% other 5.0%
Achieved: admitted req/s 498.05  rejected req/s 0.00  completed payments/s 250.66
Platform overhead latency: p50 10.94 ms  p95 55.97 ms  p99 298.14 ms
End-to-end latency (PSP fixed at 200 ms): p50 210.94 ms  p95 255.97 ms  p99 498.14 ms
Unexpected error rate: 0.000%

Resources: API CPU 152.54%, memory 223752192 bytes | DB CPU 3.78, I/O 34.1MB / 37.4GB | generator CPU 1.3%
Runtime: ThreadPool queue 5.0, Gen 2 GC/s 0.0, allocation rate 45983624 bytes/s
Database: pool wait 0.0 s, lock waits 4287.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 1854997 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 21,004 ms |
| CK2 | pass | 4,658 ms |
| CK3 | pass | 137 ms |
| CK4 | pass | 1,576 ms |
| CK5 | pass | 128 ms |
| CK6 | pass | 22 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 609 ms |
| CK10 | pass | 959 ms |

Gates passed: no
Run valid: no
Invalid because: generator dropped 2038 arrivals
Gate reasons: p99 298.14 ms is over 100 ms

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
