#!/usr/bin/env bash
# Observes what a client sees when COMMIT is waiting for standbys and something
# interrupts it (D28). Standbys are stalled by pausing their containers, so the
# fault is real, and each scenario first confirms the backend is waiting on SyncRep.
# Prints one result line per scenario. Exits non-zero only if a scenario can't run,
# because this reports behavior rather than asserting it.
set -u

COMPOSE=(docker compose -f "$(dirname "$0")/../deploy/compose/compose.yaml")
ADMIN=${DB_ADMIN_URL:-postgres://postgres:op_local_only@127.0.0.1:15432/openpayments}
DIRECT=${DB_DIRECT_URL:-postgres://op_app:op_local_only@127.0.0.1:15432/openpayments}
BOUNCER=${DB_URL:-postgres://op_app:op_local_only@127.0.0.1:16432/openpayments}

work=$(mktemp -d)
next_id=$RANDOM

admin() { PGOPTIONS="-c synchronous_commit=local" psql "$ADMIN" -XAtq -v ON_ERROR_STOP=1 -c "$1"; }
pause() { "${COMPOSE[@]}" pause "$@" >/dev/null 2>&1; }
unpause() { "${COMPOSE[@]}" unpause "$@" >/dev/null 2>&1; }

cleanup() {
  unpause standby1 2>/dev/null
  unpause standby2 2>/dev/null
  sleep 1
  admin "drop table if exists commit_probe" >/dev/null 2>&1
  rm -rf "$work"
}
trap cleanup EXIT

die() { echo "CANNOT RUN: $1"; exit 2; }

# Starts a client transaction in the background. Sets $client_pid, $out and $id.
start_commit() {
  local url=$1
  next_id=$((next_id + 1))
  id=$next_id
  out="$work/out.$id"
  cat > "$work/tx.$id.sql" <<SQL
begin;
insert into commit_probe (id) values ($id);
commit;
SQL
  psql "$url" -X -f "$work/tx.$id.sql" > "$out" 2>&1 &
  client_pid=$!
}

# Waits until a backend is blocked in COMMIT waiting for standbys.
wait_for_syncrep() {
  local tries=0 waiting
  while [ $tries -lt 50 ]; do
    waiting=$(admin "select count(*) from pg_stat_activity where wait_event = 'SyncRep'")
    [ "$waiting" -ge 1 ] && return 0
    sleep 0.2
    tries=$((tries + 1))
  done
  return 1
}

row_on_primary() { admin "select count(*) from commit_probe where id = $1"; }

row_on_standby() {
  "${COMPOSE[@]}" exec -T "$2" psql -U postgres -d openpayments -XAtc \
    "select count(*) from commit_probe where id = $1" 2>/dev/null | tr -d '\r'
}

client_said() {
  local said=""
  grep -q '^COMMIT' "$out" && said="COMMIT tag (success)"
  grep -q 'canceling wait for synchronous replication' "$out" && said="$said, WARNING canceled wait"
  grep -q 'already committed locally' "$out" && said="$said, says committed locally"
  grep -qiE 'ERROR|FATAL' "$out" && said="$said, error: $(grep -iE 'ERROR|FATAL' "$out" | head -1 | cut -c1-70)"
  grep -qiE 'closed the connection|connection to server was lost' "$out" && said="$said, connection lost"
  [ -z "$said" ] && said="nothing yet"
  echo "${said#, }"
}

report() {
  local scenario=$1 path=$2 exit_code=$3 standby_row=$4
  printf '%-10s %-22s client: %s | psql exit %s | on primary: %s | on standby after resume: %s\n' \
    "$path" "$scenario" "$(client_said)" "$exit_code" "$(row_on_primary "$id")" "$standby_row"
}

run_path() {
  local path=$1 url=$2 code

  # Control: one standby stalled. Quorum ANY 1 must not block.
  pause standby1
  start_commit "$url"
  sleep 2
  if kill -0 "$client_pid" 2>/dev/null; then
    kill -9 "$client_pid" 2>/dev/null
    code="still blocked after 2s"
  else
    wait "$client_pid"; code=$?
  fi
  unpause standby1
  report "one-standby-stalled" "$path" "$code" "$(sleep 1; row_on_standby "$id" standby2)"

  # Control: both stalled, no interruption, held past statement_timeout (1s).
  pause standby1 standby2
  start_commit "$url"
  wait_for_syncrep || die "$path: backend never reached SyncRep"
  sleep 3
  if kill -0 "$client_pid" 2>/dev/null; then held="yes"; else held="no"; fi
  visible_during=$(row_on_primary "$id")
  unpause standby1 standby2
  wait "$client_pid"; code=$?
  report "both-stalled-then-resume" "$path" "$code" "$(sleep 1; row_on_standby "$id" standby1)"
  printf '%-10s %-22s still waiting after 3s (past statement_timeout): %s | visible to others while waiting: %s\n' \
    "$path" "" "$held" "$visible_during"

  # Client cancels during the wait.
  pause standby1 standby2
  start_commit "$url"
  wait_for_syncrep || die "$path: backend never reached SyncRep"
  kill -INT "$client_pid"
  sleep 1
  if kill -0 "$client_pid" 2>/dev/null; then
    code="still waiting 1s after cancel"
    unpause standby1 standby2
    wait "$client_pid"
  else
    wait "$client_pid"; code=$?
    visible_after=$(row_on_primary "$id")
    printf '%-10s %-22s visible to others while standbys are still stalled: %s\n' "$path" "" "$visible_after"
    unpause standby1 standby2
  fi
  report "cancel-during-commit" "$path" "$code" "$(sleep 1; row_on_standby "$id" standby1)"

  # The backend is terminated during the wait, as a dropped connection would be.
  pause standby1 standby2
  start_commit "$url"
  wait_for_syncrep || die "$path: backend never reached SyncRep"
  admin "select pg_terminate_backend(pid) from pg_stat_activity where wait_event = 'SyncRep'" >/dev/null
  sleep 1
  if kill -0 "$client_pid" 2>/dev/null; then
    code="client still waiting"
    unpause standby1 standby2
    wait "$client_pid"
  else
    wait "$client_pid"; code=$?
    unpause standby1 standby2
  fi
  report "terminate-during-commit" "$path" "$code" "$(sleep 1; row_on_standby "$id" standby1)"
}

admin "drop table if exists commit_probe" || die "no database"
admin "create table commit_probe (id int primary key); grant all on commit_probe to op_app" || die "no table"
sleep 1

run_path direct "$DIRECT"
run_path pgbouncer "$BOUNCER"
