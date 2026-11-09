# Console Search Under Load

Task C13 step 5: does the console's heaviest search hurt live payments? Each run is a laptop preview.

Status: **passed** · Run date: 2026-11-09 · Commit: `f3ce132`

**Pass condition, from the console plan:** the payment p99 target is still met, and replica lag stays below 60 seconds.

## Setup

| Item | Value |
|---|---|
| Payment load | Ramp step 3: 100 req/s for 600 s, [D47](../docs/decisions.md#d47) mix, 200 ms mock PSP, `--stage phase1` |
| Search load | `benchmarks/console-search-load.sh`: the console's 31-day Payments list query (`ConsoleReadStore.ListSql`), run on standby1 as the console's read role at 5 per second for 640 s |
| Order | Full benchmark reset, then the idle run, then the run with search load. Nothing else ran or restarted in between |

## Results

| Run | Completed payments/s | Platform p50 | p95 | p99 | Errors | Replica lag (max) | Valid |
|---|---:|---:|---:|---:|---:|---:|---|
| `20261109T083749Z-f3ce132-s3-7c9c`, console idle | 50.77 | 6.74 ms | 9.98 ms | 13.78 ms | 0.000% | n/a | yes, gates pass |
| `20261109T084804Z-f3ce132-s3-51b9`, search load | 50.19 | 6.75 ms | 9.88 ms | 13.67 ms | 0.000% | 24.1 s | yes, gates pass |

The search load sent 5.00 queries per second, each taking 9.6 ms on average. CK1-CK6, CK9 and CK10 passed in both runs.

**Result:** the search load didn't measurably change payment latency. Replica lag stayed below the 60 s banner threshold. This is one pair of laptop runs at step 3; the Linux run repeats it at higher load.

**Excluded:** an earlier pair, `…075912Z-7323d59-s3-1957` and `…080927Z-7323d59-s3-39e6`, is invalid on the completed count, because another session rebuilt the API during the idle run. Its search-load run showed the same picture, p99 13.95 ms and lag at most 21.0 s.
