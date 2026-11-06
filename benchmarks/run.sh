#!/usr/bin/env bash
# Runs one ramp step: record the configuration, set the mock, drive the mix, wait for the
# drain, run op-checks, and write result.md. Refuses a dirty git tree.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
STEP=${1:-}
if [[ -z "$STEP" ]]; then
  echo "usage: benchmarks/run.sh <step> --env local|linux --stage phase0|phase1|phase2|phase3 [--psp-latency 200]" >&2
  exit 2
fi
shift

ENV=local
STAGE=phase0
PSP_LATENCY=200
PROM_URL=${PROM_URL:-http://127.0.0.1:9090}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --env) ENV=$2; shift 2 ;;
    --stage) STAGE=$2; shift 2 ;;
    --psp-latency) PSP_LATENCY=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$ENV" in local|linux) ;; *) echo "invalid --env: $ENV" >&2; exit 2 ;; esac
case "$STAGE" in phase0|phase1|phase2|phase3) ;; *) echo "invalid --stage: $STAGE" >&2; exit 2 ;; esac

cd "$REPO"
if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 is required" >&2
  exit 2
fi

DIRTY=$(git status --porcelain --untracked-files=all -- . ':(exclude)benchmarks/runs' | head -1)
if [[ -n "$DIRTY" ]]; then
  echo "refusing to run on a dirty git tree: $DIRTY" >&2
  exit 3
fi

read -r RATE DURATION < <(python3 - "$STEP" <<'PY'
import json, sys
steps = json.load(open("benchmarks/profiles/steps.json"))
step = next((s for s in steps if str(s["step"]) == sys.argv[1]), None)
if step is None:
    raise SystemExit("unknown step: " + sys.argv[1])
print(step["rate"], step["duration_seconds"])
PY
)

COMMIT=$(git rev-parse --short HEAD)
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-${COMMIT}-s${STEP}-$(printf '%04x' $RANDOM)"
RUN_DIR="$REPO/benchmarks/runs/$RUN_ID"
mkdir -p "$RUN_DIR"

TARGET_OVERRIDE=${TARGET_OVERRIDE:-api}
MOCK_URL=${MOCK_URL:-http://127.0.0.1:18080}
API_URL=${API_URL:-http://127.0.0.1:8080}
BASE_URL=$API_URL
if [[ "$TARGET_OVERRIDE" == "mock" ]]; then
  BASE_URL=$MOCK_URL
  if ! curl -sf "$MOCK_URL/health" >/dev/null; then
    echo "mock PSP is not reachable at $MOCK_URL" >&2
    exit 4
  fi
fi

MOCK_CONFIG="{}"
if curl -sf "$MOCK_URL/health" >/dev/null; then
  MOCK_CONFIG="{\"latency_ms\":${PSP_LATENCY},\"jitter_ms\":0,\"decline_rate\":0.03,\"error_rate\":0,\"callback_rate\":0}"
  curl -sf -X PUT "$MOCK_URL/admin/config" \
    -H 'Content-Type: application/json' -d "$MOCK_CONFIG" >/dev/null || true
fi

if [[ -n "${SNAPSHOT_CMD:-}" ]]; then
  bash -c "$SNAPSHOT_CMD"
fi

CHECKS_DB_URL=${CHECKS_DB_URL:-postgres://postgres:op_local_only@127.0.0.1:15432/openpayments}
EFFECTS_PATH=${EFFECTS_PATH:-$RUN_DIR/effects.csv}
REFUND_TARGETS=""
if psql "$CHECKS_DB_URL" -XAtq -c 'select 1' >/dev/null 2>&1; then
  if psql "$CHECKS_DB_URL" -XAtq -v ON_ERROR_STOP=1 -v "count=${REFUND_SEED_COUNT:-1000}" \
      -f "$REPO/benchmarks/seed-refundable.sql" >/dev/null 2>&1; then
    TARGETS_JSON="$RUN_DIR/refund-targets.json"
    psql "$CHECKS_DB_URL" -XAtq -c \
      "copy (select coalesce(json_agg(json_build_object('id', payment_id, 'merchant_id', merchant_id, 'amount_minor', amount_minor)), '[]'::json) from benchmark_refundable) to stdout" \
      > "$TARGETS_JSON" 2>/dev/null || true
    if [[ -s "$TARGETS_JSON" && "$(cat "$TARGETS_JSON")" != "[]" ]]; then
      REFUND_TARGETS=$TARGETS_JSON
    fi
  fi
fi

STARTED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
START_EPOCH=$(date +%s)
echo "run $RUN_ID: step $STEP, $RATE req/s for ${DURATION}s, $TARGET_OVERRIDE, $STAGE"
set +e
if [[ -x /usr/bin/time ]]; then
  /usr/bin/time -p env \
    STEP="$STEP" RATE="$RATE" DURATION="$DURATION" BASE_URL="$BASE_URL" \
    TARGET_OVERRIDE="$TARGET_OVERRIDE" API_TOKEN="${API_TOKEN:-}" MOCK_URL="$MOCK_URL" \
    AUTO_CAPTURE_SHARE="${AUTO_CAPTURE_SHARE:-0.5}" REFUND_TARGETS="$REFUND_TARGETS" \
    k6 run --summary-export "$RUN_DIR/k6-summary.json" benchmarks/k6/mix.js \
    > "$RUN_DIR/k6.log" 2> "$RUN_DIR/k6.time"
else
  env \
    STEP="$STEP" RATE="$RATE" DURATION="$DURATION" BASE_URL="$BASE_URL" \
    TARGET_OVERRIDE="$TARGET_OVERRIDE" API_TOKEN="${API_TOKEN:-}" MOCK_URL="$MOCK_URL" \
    AUTO_CAPTURE_SHARE="${AUTO_CAPTURE_SHARE:-0.5}" REFUND_TARGETS="$REFUND_TARGETS" \
    k6 run --summary-export "$RUN_DIR/k6-summary.json" benchmarks/k6/mix.js \
    > "$RUN_DIR/k6.log" 2> "$RUN_DIR/k6.time"
fi
K6_EXIT=$?
set -e

USER_CPU=0
SYS_CPU=0
REAL_SECONDS=0
if [[ -s "$RUN_DIR/k6.time" ]]; then
  USER_CPU=$(awk '/^user/{print $2}' "$RUN_DIR/k6.time" | tail -1)
  SYS_CPU=$(awk '/^sys/{print $2}' "$RUN_DIR/k6.time" | tail -1)
  REAL_SECONDS=$(awk '/^real/{print $2}' "$RUN_DIR/k6.time" | tail -1)
fi
CORES=$(getconf _NPROCESSORS_ONLN 2>/dev/null || nproc 2>/dev/null || echo 1)
GEN_CPU=$(python3 -c "u=float('${USER_CPU:-0}'); s=float('${SYS_CPU:-0}'); r=max(float('${REAL_SECONDS:-1}'),0.001); print(round((u+s)/r/${CORES}*100,2))")

RUN_ID="$RUN_ID" COMMIT="$COMMIT" STEP="$STEP" ENV="$ENV" STAGE="$STAGE" \
PSP_LATENCY="$PSP_LATENCY" RATE="$RATE" DURATION="$DURATION" STARTED_AT="$STARTED_AT" \
GEN_CPU="$GEN_CPU" K6_EXIT="$K6_EXIT" BASE_URL="$BASE_URL" TARGET_OVERRIDE="$TARGET_OVERRIDE" \
MOCK_CONFIG="$MOCK_CONFIG" AUTO_CAPTURE_SHARE="${AUTO_CAPTURE_SHARE:-0.5}" \
MOCK_ADDED_P99_MS="${MOCK_ADDED_P99_MS:-}" RUN_DIR="$RUN_DIR" \
PROM_URL="$PROM_URL" \
python3 - <<'PY'
import json, os
config = {
    "run_id": os.environ["RUN_ID"],
    "commit": os.environ["COMMIT"],
    "step": int(os.environ["STEP"]),
    "env": os.environ["ENV"],
    "stage": os.environ["STAGE"],
    "psp_latency_ms": int(os.environ["PSP_LATENCY"]),
    "rate": int(os.environ["RATE"]),
    "duration_seconds": int(os.environ["DURATION"]),
    "started_at": os.environ["STARTED_AT"],
    "generator_cpu_pct": float(os.environ["GEN_CPU"]),
    "k6_exit": int(os.environ["K6_EXIT"]),
    "base_url": os.environ["BASE_URL"],
    "target_override": os.environ["TARGET_OVERRIDE"],
    "mock_config": json.loads(os.environ.get("MOCK_CONFIG") or "{}"),
    "auto_capture_share": float(os.environ["AUTO_CAPTURE_SHARE"]),
    "mock_added_p99_ms": None if not os.environ["MOCK_ADDED_P99_MS"]
        else float(os.environ["MOCK_ADDED_P99_MS"]),
    "prom_url": os.environ["PROM_URL"],
    "git_dirty": False,
    "config_recorded": True,
    "api_instances": os.environ.get("API_INSTANCES", "local"),
    "db_instances": os.environ.get("DB_INSTANCES", "local"),
    "mock_instances": os.environ.get("MOCK_INSTANCES", "1"),
    "bottleneck": os.environ.get("BOTTLENECK", "not recorded"),
    "action": os.environ.get("ACTION", "not recorded"),
    "evidence": os.environ.get("EVIDENCE", "not recorded"),
    "next_step": os.environ.get("NEXT_STEP", "not recorded"),
}
with open(os.path.join(os.environ["RUN_DIR"], "config.json"), "w", encoding="utf-8") as handle:
    json.dump(config, handle, indent=2)
    handle.write("\n")
PY

sleep "${DRAIN_SECONDS:-3}"
END_EPOCH=$(date +%s)

# Copy the effect log only after the drain: CK2 must include the effects from this run,
# not just the ones already in the log when the run started.
if [[ -n "${MOCK_EFFECTS_DIR:-}" && -f "$MOCK_EFFECTS_DIR/effects.csv" ]]; then
  cp "$MOCK_EFFECTS_DIR/effects.csv" "$EFFECTS_PATH"
else
  printf 'key,op,amount_minor,currency,at\n' > "$EFFECTS_PATH"
fi

OP_CHECKS_BIN=${OP_CHECKS_BIN:-$REPO/tools/OpenPayments.Checks/bin/Debug/net10.0/OpenPayments.Checks.dll}
if [[ ! -f "$OP_CHECKS_BIN" ]]; then
  dotnet build "$REPO/tools/OpenPayments.Checks/OpenPayments.Checks.csproj" -warnaserror >/dev/null
fi

CHECKS_EXIT=0
dotnet "$OP_CHECKS_BIN" --db "$CHECKS_DB_URL" --effects "$EFFECTS_PATH" \
  --stage "$STAGE" --prom "$PROM_URL" \
  --from "$START_EPOCH" --to "$END_EPOCH" --out "$RUN_DIR/checks.json" \
  > "$RUN_DIR/checks.stdout" 2> "$RUN_DIR/checks.stderr" || CHECKS_EXIT=$?
if [[ ! -s "$RUN_DIR/checks.json" ]]; then
  printf '[]\n' > "$RUN_DIR/checks.json"
fi

if [[ "$STAGE" != "phase0" ]] && psql "$CHECKS_DB_URL" -XAtq -c 'select 1' >/dev/null 2>&1; then
  # Prometheus is optional for the local preview. Do not let a refused connection
  # (curl exit 7) abort the report through set -e.
  METRIC=$(curl -sG --data-urlencode 'query=sum(op_payments_completed_total)' \
    "${PROM_URL:-http://127.0.0.1:9090}/api/v1/query" 2>/dev/null \
    | python3 -c 'import json,sys
try:
 d=json.load(sys.stdin); r=d["data"]["result"]; print(int(float(r[0]["value"][1])) if r else "")
except Exception:
 print("")' || true)
  if [[ -n "$METRIC" ]]; then
    DB_COUNT=$(psql "$CHECKS_DB_URL" -XAtq -c "select count(*) from payments where state in ('CAPTURED','PARTIALLY_REFUNDED','REFUNDED')" 2>/dev/null || echo 0)
    printf '{"metric": %s, "db": %s}\n' "$METRIC" "$DB_COUNT" > "$RUN_DIR/counts.json"
  fi
fi

RESULT_EXIT=0
python3 "$REPO/benchmarks/report.py" --run-dir "$RUN_DIR" --stage "$STAGE" --env "$ENV" || RESULT_EXIT=$?

echo "run directory: $RUN_DIR"
if [[ "$K6_EXIT" -ne 0 || "$CHECKS_EXIT" -ne 0 || "$RESULT_EXIT" -ne 0 ]]; then
  exit 1
fi
