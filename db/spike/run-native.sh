#!/usr/bin/env bash
# Runs the spike matrix against the native stack (scripts/native-stack.sh up).
# Takes the runner's environment: ONLY, RATES, MEASURE_SECONDS, WARMUP_SECONDS, REUSE_DATA,
# SPIKE_LABEL and SPIKE_SET. Writes benchmarks/spike/<run-id>/.
set -euo pipefail
cd "$(dirname "$0")"
P=op_local_only
urls="" admins="" sep=""
for port in 26432 26433 26434 26435; do
  urls="$urls${sep}127.0.0.1:$port"
  admins="$admins${sep}postgres://postgres:$P@127.0.0.1:$port/pgbouncer"
  sep=","
done
export SPIKE_ENV=${SPIKE_ENV:-macbook} SPIKE_STACK=native
export DB_ADMIN_URL="postgres://postgres:$P@127.0.0.1:25432/openpayments"
export DB_URL="postgres://op_app:$P@$urls/openpayments?load_balance_hosts=random"
export DB_BOUNCER_ADMIN_URL="$admins"
exec python3 runner.py
