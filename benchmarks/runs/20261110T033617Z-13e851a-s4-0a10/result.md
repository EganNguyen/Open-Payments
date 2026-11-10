# Run result: preview

_a laptop run or a stage before Phase 3, so it is comparative only._

Run ID: 20261110T033617Z-13e851a-s4-0a10  
Date / commit / configuration: 2026-11-10T03:36:17Z / 13e851a / 4
Step and load profile: step 4, 250 req/s, 600 s
Instances: API local, DB local, mock 1

Request shares: sale 40.0% authorize 10.6% capture 9.3% status 14.9% refund 10.0% retry 10.1% other 5.0%
Achieved: admitted req/s 246.02  rejected req/s 0.00  completed payments/s 124.80
Platform overhead latency: p50 7.16 ms  p95 13.22 ms  p99 28.91 ms
End-to-end latency (PSP fixed at 200 ms): p50 207.16 ms  p95 213.22 ms  p99 228.91 ms
Unexpected error rate: 0.000%

Resources: API CPU 86.63%, memory 379392000 bytes | DB CPU 8.50, I/O 1.46GB / 130GB | generator CPU 0.6%
Runtime: ThreadPool queue 2.0, Gen 2 GC/s 0.0, allocation rate 9210609 bytes/s
Database: pool wait 0.0 s, lock waits 657.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 775967 bytes/s
Async: outbox age n/a, consumer lag n/a

Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

| Check | Result | Duration |
|---|---|---|
| CK1 | pass | 3,345 ms |
| CK2 | pass | 370 ms |
| CK3 | pass | 17 ms |
| CK4 | pass | 300 ms |
| CK5 | pass | 27 ms |
| CK6 | pass | 36 ms |
| CK7 | n/a | n/a |
| CK8 | n/a | n/a |
| CK9 | pass | 160 ms |
| CK10 | pass | 142 ms |

Gates passed: yes
Run valid: yes

Bottleneck found: not recorded
Action taken: not recorded
Evidence link: not recorded
Next step: not recorded
