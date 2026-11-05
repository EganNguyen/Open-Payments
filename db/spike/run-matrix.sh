#!/usr/bin/env bash
# Runs the spike matrix and writes benchmarks/spike/<run-id>/.
# Environment: SPIKE_ENV (local or linux), MEASURE_SECONDS, WARMUP_SECONDS,
# ONLY (variant names, comma-separated), DB_ADMIN_URL, DB_URL, DB_BOUNCER_ADMIN_URL,
# and on Linux SPIKE_CPU_COMMAND, which prints the primary's CPU percent.
set -euo pipefail
cd "$(dirname "$0")"
exec python3 runner.py
