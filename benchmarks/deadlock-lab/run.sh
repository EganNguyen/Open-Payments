#!/usr/bin/env bash
# Starts a throwaway PostgreSQL 18.6, runs the lab in both modes, keeps the server's
# deadlock reports, then removes the container. Usage: benchmarks/deadlock-lab/run.sh [rounds]
set -euo pipefail
cd "$(dirname "$0")"
ROUNDS=${1:-5}
# D1: deadlock_timeout 100 ms below a 200 ms lock_timeout, the pair the stack runs (D54).
NAME=op-deadlock-lab
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_PASSWORD=lab -p 127.0.0.1:35432:5432 postgres:18.6 \
  -c max_connections=200 -c log_lock_waits=on -c deadlock_timeout=${DEADLOCK_TIMEOUT:-100ms} -c lock_timeout=${LOCK_TIMEOUT:-200ms} >/dev/null
trap 'docker rm -f "$NAME" >/dev/null 2>&1 || true' EXIT
for _ in $(seq 1 60); do
  docker exec "$NAME" pg_isready -U postgres -q && break
  sleep 1
done
docker exec "$NAME" psql -U postgres -Atc "show deadlock_timeout" | sed 's/^/deadlock_timeout: /'
dotnet run -c Release ${OPENPAYMENTS_ROOT:+-p:OpenPaymentsRoot=$OPENPAYMENTS_ROOT} -- \
  "Host=127.0.0.1;Port=35432;Username=postgres;Password=lab;Database=postgres" "$ROUNDS"
status=$?
docker logs "$NAME" 2>&1 | grep -A6 "deadlock detected" > deadlocks.log || true
echo "server deadlock reports: $(grep -c 'deadlock detected' deadlocks.log || true), first one:"
grep -m1 -A6 "deadlock detected" deadlocks.log || true
exit $status
