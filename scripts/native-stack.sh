#!/usr/bin/env bash
# The native macOS stack: a primary, two quorum standbys and four PgBouncer processes,
# with real flushes to the SSD. Usage: scripts/native-stack.sh up|down|status|reset
# CELL=2 runs a second, independent cell beside the first, on its own ports and data.
# `reset` deletes this stack's data directory, and nothing else.
set -euo pipefail

# Without a valid locale, macOS makes the postmaster multithreaded and it refuses to start.
export LC_ALL=en_US.UTF-8

REPO=$(cd "$(dirname "$0")/.." && pwd)
PG=${PG_BIN:-/opt/homebrew/opt/postgresql@18/bin}
PGBOUNCER=${PGBOUNCER_BIN:-/opt/homebrew/opt/pgbouncer/bin/pgbouncer}
CELL=${CELL:-1}
SUFFIX=$([ "$CELL" = 1 ] || echo "-cell$CELL")
BASE=${OP_NATIVE_DIR:-$HOME/.local/share/open-payments-native}$SUFFIX
OFFSET=$(( (CELL - 1) * 100 ))
RUN=$BASE/run
LOG=$BASE/log
PASSWORD=op_local_only          # local-only development value, bound to 127.0.0.1
PRIMARY_PORT=$(( 25432 + OFFSET ))
PRIMARY_BUFFERS=${PRIMARY_SHARED_BUFFERS:-}
STANDBY_BUFFERS=${STANDBY_SHARED_BUFFERS:-6GB}

port_of() { case $1 in primary) echo $(( 25432 + OFFSET ));; standby1) echo $(( 25433 + OFFSET ));; standby2) echo $(( 25434 + OFFSET ));; esac; }
cluster_of() { [ "$CELL" = 1 ] && echo "$1" || echo "${1}c$CELL"; }
pool_port() { echo $(( 26431 + OFFSET + $1 )); }
pool_id() { echo $(( (CELL - 1) * 10 + $1 )); }  # samplers tell PgBouncers apart by this number

start_pg() { # name, extra settings
  local name=$1 port; port=$(port_of "$name"); shift
  "$PG/pg_ctl" -w -t 120 -D "$BASE/$name" -l "$LOG/$name.log" \
    -o "-p $port -c cluster_name=$(cluster_of "$name") -c unix_socket_directories=$RUN $*" start >/dev/null
}

init_primary() {
  local pwfile="$BASE/.pw"
  (umask 077; echo "$PASSWORD" > "$pwfile")
  "$PG/initdb" -D "$BASE/primary" -U postgres --auth=scram-sha-256 --pwfile="$pwfile" -E UTF8 --locale=C >/dev/null
  rm -f "$pwfile"
  {
    echo "include '$REPO/deploy/compose/postgres/primary.macbook.conf'"
    echo "include '$REPO/deploy/native/native.conf'"
  } >> "$BASE/primary/postgresql.conf"
  echo "host replication replicator 127.0.0.1/32 scram-sha-256" >> "$BASE/primary/pg_hba.conf"
  start_pg primary "-c synchronous_standby_names=''"
  PGPASSWORD=$PASSWORD PGOPTIONS="-c synchronous_commit=local" "$PG/psql" -h 127.0.0.1 -p $PRIMARY_PORT -U postgres -d postgres -Xq -v ON_ERROR_STOP=1 <<SQL
create role replicator with replication login password '$PASSWORD';
create role op_app with login password '$PASSWORD';
alter role op_app set lock_timeout = '200ms';
alter role op_app set statement_timeout = '1s';
alter role op_app set idle_in_transaction_session_timeout = '2s';
create database openpayments;
\c openpayments
grant all on schema public to op_app;
SQL
  "$PG/pg_ctl" -w -D "$BASE/primary" stop -m fast >/dev/null
}

init_standby() {
  PGPASSWORD=$PASSWORD "$PG/pg_basebackup" \
    --dbname "host=127.0.0.1 port=$PRIMARY_PORT user=replicator application_name=$1" \
    --pgdata "$BASE/$1" --wal-method=stream --write-recovery-conf --create-slot --slot "$1" --checkpoint=fast
  chmod 700 "$BASE/$1"
}

up() {
  [ -x "$PG/postgres" ] && [ -x "$PGBOUNCER" ] || { echo "Run: brew install postgresql@18 pgbouncer" >&2; exit 1; }
  mkdir -p "$RUN" "$LOG"
  ulimit -n 20000
  [ -d "$BASE/primary" ] || init_primary
  pg_running primary || start_pg primary ${PRIMARY_BUFFERS:+-c shared_buffers=$PRIMARY_BUFFERS}
  for s in standby1 standby2; do
    [ -d "$BASE/$s" ] || init_standby "$s"
    pg_running "$s" || start_pg "$s" "-c shared_buffers=$STANDBY_BUFFERS -c synchronous_standby_names=''"
  done
  for i in 1 2 3 4; do
    n=$(pool_id "$i")
    sed -e "s#@RUN@#$RUN#g" -e "s#@LOG@#$LOG#g" -e "s#@REPO@#$REPO#g" -e "s#@N@#$n#g" \
        -e "s#@PG_PORT@#$PRIMARY_PORT#g" -e "s#@PORT@#$(pool_port "$i")#g" \
        "$REPO/deploy/native/pgbouncer.ini.template" > "$RUN/pgbouncer$n.ini"
    bouncer_running "$n" || "$PGBOUNCER" -d "$RUN/pgbouncer$n.ini"
  done
  status
}

pg_running() { [ -d "$BASE/$1" ] && "$PG/pg_ctl" -D "$BASE/$1" status >/dev/null 2>&1; }
bouncer_running() { [ -f "$RUN/pgbouncer$1.pid" ] && kill -0 "$(cat "$RUN/pgbouncer$1.pid")" 2>/dev/null; }

down() {
  for i in 1 2 3 4; do
    n=$(pool_id "$i")
    if bouncer_running "$n"; then kill "$(cat "$RUN/pgbouncer$n.pid")"; fi
  done
  for s in standby2 standby1 primary; do
    if pg_running "$s"; then "$PG/pg_ctl" -w -t 120 -D "$BASE/$s" stop -m fast >/dev/null; fi
  done
}

status() { # Exits non-zero unless every service is healthy and replication is in quorum
  local bad=0 p q
  q() { PGPASSWORD=$PASSWORD "$PG/psql" -h 127.0.0.1 -p "$1" -U postgres -d "$2" -XAtq -c "$3"; }
  for s in primary standby1 standby2; do
    p=$(port_of "$s")
    if "$PG/pg_isready" -q -h 127.0.0.1 -p "$p"; then echo "ok    $s on $p"; else echo "FAIL  $s on $p"; bad=1; fi
  done
  for i in 1 2 3 4; do
    n=$(pool_id "$i"); p=$(pool_port "$i")
    if q "$p" pgbouncer "show version" >/dev/null 2>&1; then echo "ok    pgbouncer$n on $p"; else echo "FAIL  pgbouncer$n on $p"; bad=1; fi
  done
  [ "$(q "$PRIMARY_PORT" postgres 'show wal_sync_method' 2>/dev/null)" = fsync_writethrough ] && echo "ok    wal_sync_method = fsync_writethrough" || { echo "FAIL  wal_sync_method"; bad=1; }
  [ "$(q $PRIMARY_PORT postgres 'show synchronous_standby_names' 2>/dev/null)" = "ANY 1 (standby1, standby2)" ] && echo "ok    quorum commit set" || { echo "FAIL  quorum commit"; bad=1; }
  [ "$(q $PRIMARY_PORT postgres "select count(*) from pg_stat_replication where sync_state = 'quorum' and state = 'streaming'" 2>/dev/null)" = 2 ] && echo "ok    2 standbys streaming in quorum" || { echo "FAIL  standbys not both streaming in quorum"; bad=1; }
  return $bad
}

reset() {
  down
  case "$BASE" in
    *open-payments-native*) rm -rf "$BASE" ;;
    *) echo "Refusing to delete $BASE" >&2; exit 1 ;;
  esac
}

"${1:?usage: native-stack.sh up|down|status|reset}"
