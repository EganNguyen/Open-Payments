#!/usr/bin/env bash
# Finds what limits throughput: offers far more load than the system can take,
# changing one suspect at a time. Needs the stack running with `--profile spike`.
# Compare achieved rates only. Latency at this load is queueing and means nothing.
set -uo pipefail
cd "$(dirname "$0")"

P=op_local_only
ONE="postgres://op_app:$P@pgbouncer:6432/openpayments"
FOUR="postgres://op_app:$P@pgbouncer:6432,pgbouncer2:6432,pgbouncer3:6432,pgbouncer4:6432/openpayments?load_balance_hosts=random"
DIRECT="postgres://op_app:$P@primary:5432/openpayments"
ADMIN1="postgres://postgres:$P@pgbouncer:6432/pgbouncer"
ADMIN4="$ADMIN1,postgres://postgres:$P@pgbouncer2:6432/pgbouncer,postgres://postgres:$P@pgbouncer3:6432/pgbouncer,postgres://postgres:$P@pgbouncer4:6432/pgbouncer"

common=(SPIKE_ENV=macbook REUSE_DATA="${REUSE_DATA-1}" ONLY=baseline RATES="${RATE:-10000}" MEASURE_SECONDS="${MEASURE_SECONDS:-40}" WARMUP_SECONDS="${WARMUP_SECONDS:-5}")

step() { # label, generator, app url, pooler admin urls, settings
  echo "== $1"
  env "${common[@]}" SPIKE_LABEL="limit-$1" SPIKE_GENERATOR="$2" DB_URL="$3" DB_BOUNCER_ADMIN_URL="$4" SPIKE_SET="$5" python3 runner.py 2>&1 | grep -E 'rate |Error|error|Traceback'
}

G=open-payments-generator-1

system() { psql "${DB_ADMIN_URL:-postgres://postgres:$P@127.0.0.1:15432/openpayments}" -XAtq -c "$1" -c "select pg_reload_conf()" >/dev/null; }

layers() { # What sits above the database: port forwarding, pooler processes, pool size
  step host-1pool     ""  "postgres://op_app:$P@127.0.0.1:16432/openpayments" "postgres://postgres:$P@127.0.0.1:16432/pgbouncer" '{"clients":256}'
  step vm-1pool       $G  "$ONE"    "$ADMIN1" '{"clients":256}'
  step vm-1pool-800   $G  "$ONE"    "$ADMIN1" '{"clients":800,"threads":8}'
  step vm-4pool       $G  "$FOUR"   "$ADMIN4" '{"clients":800,"threads":8}'
  step vm-4pool-400   $G  "$FOUR"   "$ADMIN4" '{"clients":800,"threads":8,"pool":400}'
  step vm-direct      $G  "$DIRECT" ""        '{"clients":800,"threads":8}'
  step vm-4pool-prep  $G  "$FOUR"   "$ADMIN4" '{"clients":800,"threads":8,"pool":400,"protocol":"prepared"}'
  step vm-direct-prep $G  "$DIRECT" ""        '{"clients":800,"threads":8,"protocol":"prepared"}'
}

settings() { # With those removed, what the cheap settings give
  B='"clients":800,"threads":8'
  step pool32      $G "$FOUR" "$ADMIN4" "{$B,\"pool\":32}"
  step pool64      $G "$FOUR" "$ADMIN4" "{$B,\"pool\":64}"
  step pool100     $G "$FOUR" "$ADMIN4" "{$B,\"pool\":100}"
  step pool100prep $G "$FOUR" "$ADMIN4" "{$B,\"pool\":100,\"protocol\":\"prepared\"}"
  step saleonly    $G "$FOUR" "$ADMIN4" "{$B,\"pool\":100,\"scripts\":{\"sale\":1}}"
  step salepipe    $G "$FOUR" "$ADMIN4" "{$B,\"pool\":100,\"scripts\":{\"sale\":1},\"pipeline\":1}"
  system "alter system set commit_delay = 100"
  system "alter system set commit_siblings = 5"
  step delay100    $G "$FOUR" "$ADMIN4" "{$B,\"pool\":100}"
  system "alter system reset commit_delay"
  system "alter system reset commit_siblings"
}

native() { # The native stack with real flushes. Set REUSE_DATA= to load data first.
  local urls="" admins="" sep=""
  for port in 26432 26433 26434 26435; do
    urls="$urls${sep}127.0.0.1:$port"
    admins="$admins${sep}postgres://postgres:$P@127.0.0.1:$port/pgbouncer"
    sep=","
  done
  local app="postgres://op_app:$P@$urls/openpayments?load_balance_hosts=random"
  export SPIKE_STACK=native DB_ADMIN_URL="postgres://postgres:$P@127.0.0.1:25432/openpayments"
  local schema=${SCHEMA:-a-hash}
  for pool in ${POOLS:-100}; do
    step "native-$schema-pool$pool${DIAG:+-$DIAG}" "" "$app" "$admins" "{\"clients\":800,\"threads\":8,\"pool\":$pool,\"schema\":\"$schema\"${EXTRA:+,$EXTRA}}"
  done
}

all_nodes() {
  for port in 25432 25433 25434; do
    DB_ADMIN_URL="postgres://postgres:$P@127.0.0.1:$port/openpayments" system "$1"
  done
}

diagnostics() { # Settings the design forbids, run only to split the cost apart. Never a result.
  export DB_ADMIN_URL="postgres://postgres:$P@127.0.0.1:25432/openpayments"
  # ALTER SYSTEM reaches only the server it runs on, so this must set all three.
  all_nodes "alter system set wal_sync_method = fsync"
  POOLS=${DIAG_POOL:-100} DIAG=unsafe-fsync native
  all_nodes "alter system reset wal_sync_method"
  system "alter system set synchronous_standby_names = ''"
  POOLS=${DIAG_POOL:-100} DIAG=no-quorum native
  system "alter system reset synchronous_standby_names"
}

"${@:-layers}"
