# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T043457Z-28e4bc5-s100-1e77  
Date / commit / configuration: 2026-11-10T04:34:57Z / 28e4bc5 / 100
Step and load profile: step 100, 500 req/s, 3600 s
Instances: API local, DB local, mock 1

Request shares: sale 40.1% authorize 10.3% capture 9.7% status 15.0% refund 10.0% retry 9.9% other 5.0%
Achieved: admitted req/s 495.88  rejected req/s 0.00  completed payments/s 249.62
Platform overhead latency: p50 9.09 ms  p95 76.56 ms  p99 589.57 ms
End-to-end latency (PSP fixed at 200 ms): p50 209.09 ms  p95 276.56 ms  p99 789.57 ms
Unexpected error rate: 0.000%

Resources: API CPU 138.57%, memory 238399488 bytes | DB CPU 4.49, I/O 1.57GB / 205GB | generator CPU 1.1%
Runtime: ThreadPool queue 3.0, Gen 2 GC/s 0.0, allocation rate 45985304 bytes/s
Database: pool wait 0.0 s, lock waits 5017.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 1658981 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 19,887 ms |
| CK2 | pass | 4,025 ms |
| CK3 | pass | 117 ms |
| CK4 | pass | 2,438 ms |
| CK5 | pass | 145 ms |
| CK6 | pass | 31 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 1,264 ms |
| CK10 | pass | 814 ms |

Gates passed: no
Run valid: no
Invalid because: generator dropped 9866 arrivals
Gate reasons: p99 589.57 ms is over 100 ms

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
