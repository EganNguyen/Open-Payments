#!/usr/bin/env bash
# Runs the E1 probe on a throwaway Postgres and PgBouncer, and removes them afterward.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
W=$(mktemp -d)
PG=/opt/homebrew/opt/postgresql@18/bin
LOG=$W/pg.log
export LC_ALL=en_US.UTF-8 PGHOST=127.0.0.1 PGUSER=postgres
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

cleanup() {
  [ -f "$W/bouncer.pid" ] && kill "$(cat "$W/bouncer.pid")" 2>/dev/null
  "$PG/pg_ctl" -D "$W/data" -m immediate stop >/dev/null 2>&1
  rm -rf "$W"
}
trap cleanup EXIT

"$PG/initdb" -D "$W/data" -U postgres --auth=trust -E UTF8 --locale=C >/dev/null || exit 1
cat >> "$W/data/postgresql.conf" <<EOF
port = 35432
listen_addresses = '127.0.0.1'
unix_socket_directories = ''
synchronous_commit = on
EOF
"$PG/pg_ctl" -D "$W/data" -l "$LOG" -w start >/dev/null || exit 1
"$PG/psql" -p 35432 -d postgres -XAtq -c "create database probe" || exit 1
"$PG/psql" -p 35432 -d probe -XAtq -c "create table commit_probe(id int primary key)" || exit 1

cat > "$W/bouncer.ini" <<EOF
[databases]
probe = host=127.0.0.1 port=35432 dbname=probe user=postgres
[pgbouncer]
listen_addr = 127.0.0.1
listen_port = 36432
unix_socket_dir =
auth_type = any
pool_mode = transaction
default_pool_size = 20
query_wait_timeout = 5
ignore_startup_parameters = extra_float_digits
logfile = $W/bouncer.log
pidfile = $W/bouncer.pid
EOF
/opt/homebrew/bin/pgbouncer -d "$W/bouncer.ini" || exit 1
sleep 1

dotnet run --project "$HERE/probe.csproj" -c Release -- "$LOG"
echo "probe exit: $?"

# A client that vanishes without a cancel: kill -9 psql during the wait, directly and through PgBouncer.
for target in "direct 35432 900001" "pgbouncer 36432 900002"; do
  set -- $target
  "$PG/psql" -p 35432 -d probe -XAtq -c "alter system set synchronous_standby_names = 'ANY 1 (standby1, standby2)'" -c "select pg_reload_conf()" >/dev/null
  sleep 0.5
  before=$(grep -c 'canceling wait for synchronous replication' "$LOG")
  "$PG/psql" -p "$2" -d probe -XAtq -c "insert into commit_probe values ($3)" >/dev/null 2>&1 &
  pid=$!
  sleep 1
  kill -9 "$pid"; wait "$pid" 2>/dev/null
  sleep 2
  after=$(grep -c 'canceling wait for synchronous replication' "$LOG")
  vis=$("$PG/psql" -p 35432 -d probe -XAt -c "set synchronous_commit = local" -c "select count(*) from commit_probe where id = $3" | tail -1)
  wait_n=$("$PG/psql" -p 35432 -d probe -XAt -c "select count(*) from pg_stat_activity where wait_event = 'SyncRep'" | tail -1)
  echo "[$1] client killed: cancels logged=$((after-before)); row visible=$vis; backends in SyncRep=$wait_n"
  "$PG/psql" -p 35432 -d probe -XAtq -c "alter system set synchronous_standby_names = ''" -c "select pg_reload_conf()" >/dev/null
  sleep 1
done
echo "--- server warnings"
grep -E 'WARNING|FATAL' "$LOG" | sed -E 's/^[0-9-]+ [0-9:.]+ [A-Z+0-9]+ //' | sort | uniq -c
echo "--- versions"
"$PG/postgres" --version; /opt/homebrew/bin/pgbouncer --version | head -1; dotnet --version
