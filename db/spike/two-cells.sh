#!/usr/bin/env bash
# Measures the full mix (V13) on one cell, then on two cells with the same total memory and
# data. Both cells share one SSD, so this can't show what cells do with their own disks.
# Environment: PAYMENT_RATES (payments/s), POOL (total), SCHEMA, MEASURE_SECONDS, WARMUP_SECONDS,
# PREFILL and REFUNDABLE (per cell in two_cells).
set -uo pipefail
cd "$(dirname "$0")"

P=op_local_only
SCHEMA=${SCHEMA:-c-hash-all}
POOL=${POOL:-200}
PAYMENT_RATES=${PAYMENT_RATES:-500 750 1000 1250 1500}
export MEASURE_SECONDS=${MEASURE_SECONDS:-60} WARMUP_SECONDS=${WARMUP_SECONDS:-10}
export SPIKE_ENV=macbook SPIKE_STACK=native ONLY=V13

# V13 runs 90 sales in every 211 scripts, so one payment/s needs 211/90 scripts/s.
scripts_per_s() { echo $(( $1 * 211 / 90 / $2 )); }

urls() { # cell
  local offset=$(( ($1 - 1) * 100 )) out="" sep=""
  for i in 0 1 2 3; do out="$out${sep}127.0.0.1:$(( 26432 + offset + i ))"; sep=","; done
  echo "postgres://op_app:$P@$out/openpayments?load_balance_hosts=random"
}

admins() { # cell
  local offset=$(( ($1 - 1) * 100 )) out="" sep=""
  for i in 0 1 2 3; do out="$out${sep}postgres://postgres:$P@127.0.0.1:$(( 26432 + offset + i ))/pgbouncer"; sep=","; done
  echo "$out"
}

run_cell() { # cell, label, script rate, reuse data (1 or empty), settings
  local offset=$(( ($1 - 1) * 100 ))
  env SPIKE_LABEL="$2" RATES="$3" REUSE_DATA="$4" SPIKE_SET="$5" \
    DB_ADMIN_URL="postgres://postgres:$P@127.0.0.1:$(( 25432 + offset ))/openpayments" \
    DB_URL="$(urls "$1")" DB_BOUNCER_ADMIN_URL="$(admins "$1")" python3 runner.py 2>&1 | grep -E 'rate |Error|Traceback' | sed "s/^/cell $1: /"
}

one_cell() {
  local settings="{\"clients\":800,\"threads\":8,\"pool\":$POOL,\"schema\":\"$SCHEMA\",\"prefill\":${PREFILL:-5000000},\"refundable\":${REFUNDABLE:-100000}}" reuse=""
  ../../scripts/native-stack.sh up >/dev/null || exit 1
  for rate in $PAYMENT_RATES; do
    echo "== one cell, $rate payments/s offered"
    run_cell 1 "one-cell-$rate" "$(scripts_per_s "$rate" 1)" "$reuse" "$settings"
    reuse=1
  done
}

two_cells() {
  local settings="{\"clients\":400,\"threads\":4,\"pool\":$(( POOL / 2 )),\"schema\":\"$SCHEMA\",\"prefill\":${PREFILL:-2500000},\"merchants\":50,\"refundable\":${REFUNDABLE:-50000}}"
  ../../scripts/native-stack.sh down
  export PRIMARY_SHARED_BUFFERS=5GB STANDBY_SHARED_BUFFERS=3GB
  CELL=1 ../../scripts/native-stack.sh up >/dev/null || exit 1
  CELL=2 ../../scripts/native-stack.sh up >/dev/null || exit 1
  # shellcheck disable=SC2064
  trap "CELL=2 ../../scripts/native-stack.sh down" EXIT
  # Load the cells one after the other, so the measured runs start together.
  for cell in 1 2; do
    MEASURE_SECONDS=5 WARMUP_SECONDS=0 run_cell "$cell" "two-cells-load" 10 "" "$settings"
  done
  for rate in $PAYMENT_RATES; do
    echo "== two cells, $rate payments/s offered in total"
    for cell in 1 2; do
      run_cell "$cell" "two-cells-$rate-c$cell" "$(scripts_per_s "$rate" 2)" 1 "$settings" &
    done
    wait
  done
}

"${@:-one_cell}"
