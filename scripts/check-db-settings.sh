#!/usr/bin/env bash
# Exits non-zero unless the database is configured as the design requires:
# quorum replication (D7), deadlock detection below the lock timeout (D1), and
# the application role's timeouts, seen directly and through PgBouncer.
set -u

# Local-only development values, bound to 127.0.0.1 by deploy/compose/compose.yaml.
DB_ADMIN_URL=${DB_ADMIN_URL:-postgres://postgres:op_local_only@127.0.0.1:15432/openpayments}
DB_DIRECT_URL=${DB_DIRECT_URL:-postgres://op_app:op_local_only@127.0.0.1:15432/openpayments}
DB_URL=${DB_URL:-postgres://op_app:op_local_only@127.0.0.1:16432/openpayments}

failed=0

# check <label> <url> <sql> <expected>
check() {
  local label=$1 url=$2 sql=$3 expected=$4 actual
  if ! actual=$(psql "$url" -XAtq -v ON_ERROR_STOP=1 -c "$sql" 2>&1); then
    printf 'FAIL  %s: could not query: %s\n' "$label" "$actual"
    failed=1
    return
  fi
  if [ "$actual" = "$expected" ]; then
    printf 'ok    %s = %s\n' "$label" "$actual"
  else
    printf 'FAIL  %s: expected %s, got %s\n' "$label" "$expected" "$actual"
    failed=1
  fi
}

check "synchronous_standby_names" "$DB_ADMIN_URL" "show synchronous_standby_names" "ANY 1 (standby1, standby2)"
check "synchronous_commit" "$DB_ADMIN_URL" "show synchronous_commit" "on"
check "quorum standbys" "$DB_ADMIN_URL" \
  "select count(*) from pg_stat_replication where sync_state = 'quorum' and application_name in ('standby1','standby2')" "2"
check "deadlock_timeout" "$DB_ADMIN_URL" "show deadlock_timeout" "100ms"
check "log_lock_waits" "$DB_ADMIN_URL" "show log_lock_waits" "on"

for path in direct pgbouncer; do
  url=$DB_DIRECT_URL
  [ "$path" = pgbouncer ] && url=$DB_URL
  check "$path lock_timeout" "$url" "show lock_timeout" "200ms"
  check "$path statement_timeout" "$url" "show statement_timeout" "1s"
  check "$path idle_in_transaction_session_timeout" "$url" "show idle_in_transaction_session_timeout" "2s"
done

if [ "$failed" -ne 0 ]; then
  echo "Database settings check FAILED"
  exit 1
fi
echo "Database settings check passed"
