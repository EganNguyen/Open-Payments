#!/usr/bin/env bash
# Task C13 step 5: the console's 31-day Payments list query on standby1 at 5 per second, as the
# console's read role, for DURATION seconds, sampling replica lag every 5 s into OUT_DIR.
# It runs the query the page runs (ConsoleReadStore.ListSql), with this deployment's merchant.
set -euo pipefail
DURATION=${1:?usage: console-search-load.sh <seconds> <out-dir>}
OUT=${2:?usage: console-search-load.sh <seconds> <out-dir>}
MERCHANT=${MERCHANT:-11111111-1111-7111-8111-111111111111}
mkdir -p "$OUT"
STANDBY=${STANDBY_CONTAINER:-open-payments-standby1-1}

docker exec -i "$STANDBY" sh -c 'cat > /tmp/console-list.sql' <<SQL
select id, merchant_id, state, amount_minor, currency, captured_minor, refunded_minor, refund_pending_minor, created_at, updated_at, authorized_until
from payments
where merchant_id = any('{$MERCHANT}'::uuid[])
  and created_at >= now() - interval '31 days' and created_at < now()
  and (null::text is null or state = null::text)
  and (null::bigint is null or amount_minor >= null::bigint)
  and (null::bigint is null or amount_minor <= null::bigint)
  and (null::timestamptz is null or (created_at, id) < (null::timestamptz, null::uuid))
order by created_at desc, id desc
limit 51;
SQL

(
  end=$(( $(date +%s) + DURATION ))
  while [ "$(date +%s)" -lt "$end" ]; do
    lag=$(docker exec "$STANDBY" psql -U postgres -d openpayments -XAtc \
      "select coalesce(extract(epoch from now() - pg_last_xact_replay_timestamp()), 0)" 2>/dev/null || echo "n/a")
    echo "$(date -u +%FT%TZ) $lag" >> "$OUT/replica-lag.txt"
    sleep 5
  done
) &
SAMPLER=$!

docker exec -e PGPASSWORD=op_local_only "$STANDBY" pgbench -n -U console_read -h 127.0.0.1 -d openpayments \
  -f /tmp/console-list.sql -R 5 -T "$DURATION" -P 60 > "$OUT/pgbench.txt" 2>&1 || true
wait "$SAMPLER" || true
echo "max replica lag (s): $(awk '$2 != "n/a" {if ($2 > m) m = $2} END {print m + 0}' "$OUT/replica-lag.txt")" | tee -a "$OUT/summary.txt"
grep -E "latency average|tps|number of transactions actually processed|statement timeout|error" "$OUT/pgbench.txt" | tee -a "$OUT/summary.txt" || true
