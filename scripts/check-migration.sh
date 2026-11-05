#!/usr/bin/env bash
# Proves the migrations on a throwaway database: the runner records and skips versions, refuses
# an edited migration, and the schema enforces the money rules. Deletes that database afterward.
# DB_ADMIN_URL points at any database on the server to test against.
set -u

REPO=$(cd "$(dirname "$0")/.." && pwd)
ADMIN=${DB_ADMIN_URL:-postgres://postgres:op_local_only@127.0.0.1:15432/openpayments}
TEST_DB=op_migration_check
TEST_URL="${ADMIN%/*}/$TEST_DB"
APP_URL=$(echo "$TEST_URL" | sed -E 's#//postgres:#//op_app:#')
export PGOPTIONS="-c synchronous_commit=local"
failed=0

pass() { printf 'ok    %s\n' "$1"; }
fail() { printf 'FAIL  %s\n' "$1"; failed=1; }

run() { psql "$1" -XAtq -v ON_ERROR_STOP=1 -c "$2" 2>&1; }

# accepts <label> <sql>: the statement must succeed.
accepts() { if out=$(run "$TEST_URL" "$2"); then pass "$1"; else fail "$1: $out"; fi; }

# rejects <label> <url> <expected error text> <sql>: must fail, with that error.
rejects() {
  if out=$(run "$2" "$4"); then fail "$1: was accepted"
  elif echo "$out" | grep -q "$3"; then pass "$1"
  else fail "$1: wrong error: $out"; fi
}

psql "$ADMIN" -XAtq -c "drop database if exists $TEST_DB" -c "create database $TEST_DB" || exit 1
cleanup() { psql "$ADMIN" -XAtq -c "drop database if exists $TEST_DB"; rm -rf "${WORK:-}"; }
trap cleanup EXIT

# The runner
if DB_ADMIN_URL=$TEST_URL "$REPO/scripts/migrate.sh" >/dev/null 2>&1; then pass "first run applies"; else fail "first run applies"; fi
if DB_ADMIN_URL=$TEST_URL "$REPO/scripts/migrate.sh" >/dev/null 2>&1; then pass "second run succeeds"; else fail "second run succeeds"; fi
count=$(run "$TEST_URL" "select count(*) from schema_migrations where version = '0001_initial'" 2>&1)
[ "$count" = 1 ] && pass "version recorded once" || fail "version recorded once: $count"

WORK=$(mktemp -d)
cp "$REPO"/db/migrations/*.sql "$WORK/"
printf '%s\n' '-- edited' >> "$WORK/0001_initial.sql"
if DB_ADMIN_URL=$TEST_URL MIGRATIONS_DIR=$WORK "$REPO/scripts/migrate.sh" >/dev/null 2>&1; then fail "edited migration refused"; else pass "edited migration refused"; fi

printf '%s\n' 'create table should_not_exist (id int);' 'select no_such_function();' > "$WORK/0002_broken.sql"
cp "$REPO/db/migrations/0001_initial.sql" "$WORK/0001_initial.sql"
if DB_ADMIN_URL=$TEST_URL MIGRATIONS_DIR=$WORK "$REPO/scripts/migrate.sh" >/dev/null 2>&1; then fail "broken migration fails"; else pass "broken migration fails"; fi
exists=$(run "$TEST_URL" "select count(*) from pg_class where relname = 'should_not_exist'")
[ "$exists" = 0 ] && pass "broken migration rolls back" || fail "broken migration rolls back"

# Fixtures
M=00000000-0000-7000-8000-000000000001
PA=00000000-0000-7000-8000-000000000002
P1=00000000-0000-7000-8000-000000000011
P2=00000000-0000-7000-8000-000000000012
A1=00000000-0000-7000-8000-000000000021
A2=00000000-0000-7000-8000-000000000022
A3=00000000-0000-7000-8000-000000000023
A4=00000000-0000-7000-8000-000000000024
A5=00000000-0000-7000-8000-000000000025
L1=00000000-0000-7000-8000-000000000031
L2=00000000-0000-7000-8000-000000000032
AC=00000000-0000-7000-8000-000000000041

accepts "fixtures load" "
  insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s) values ('$PA', '$M', 'mock', 'test', 'ref', 100);
  insert into accounts (id, merchant_id, account_type, class, class_of_money, currency) values ('$AC', '$M', 'merchant_payable', 'clearing', 'real', 'USD');
  insert into payments (id, merchant_id, state, amount_minor, currency, expires_at) values
    ('$P1', '$M', 'PSP_PENDING', 100, 'USD', now() + interval '1 day'),
    ('$P2', '$M', 'PSP_PENDING', 100, 'USD', now() + interval '1 day');
  insert into payment_attempts (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, method_ref, method_type) values
    ('$A1', '$P1', '$M', 'authorize', 'mock', '$PA', 'SUCCEEDED', 'tok', 'card'),
    ('$A5', '$P2', '$M', 'authorize', 'mock', '$PA', 'SUCCEEDED', 'tok', 'card');"

attempt() { # attempt id, payment id, operation, target attempt
  echo "insert into payment_attempts (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, method_ref, method_type, target_attempt_id)
        values ('$1', '$2', '$M', '$3', 'mock', '$PA', 'PENDING', 'tok', 'card', ${4:-null})"
}

# Attempts
rejects "second live authorize on a payment" "$TEST_URL" "payment_attempts_[0-9]*_one_live_authorize" "$(attempt "$A2" "$P1" sale)"
accepts "first capture" "$(attempt "$A2" "$P1" capture)"
rejects "capture racing a capture" "$TEST_URL" "payment_attempts_[0-9]*_one_live_capture" "$(attempt "$A3" "$P1" capture)"
rejects "void racing a capture" "$TEST_URL" "payment_attempts_[0-9]*_one_live_capture" "$(attempt "$A3" "$P1" void)"
accepts "late void beside a capture" "$(attempt "$A3" "$P1" void "'$A1'")"
rejects "second late void of an attempt" "$TEST_URL" "payment_attempts_[0-9]*_one_late_void" "$(attempt "$A4" "$P1" void "'$A1'")"
rejects "late void of another payment's attempt" "$TEST_URL" "foreign key" "$(attempt "$A4" "$P2" void "'$A1'")"
rejects "attempt for a missing payment" "$TEST_URL" "foreign key" "$(attempt "$A4" "$A4" sale)"

# Ledger
entries() { # txn, debit amount, credit amount
  echo "insert into ledger_entries (txn_id, entry_no, merchant_id, account_id, subject_id, direction, amount_minor, currency) values
        ('$1', 1, '$M', '$AC', '$P1', 'D', $2, 'USD'), ('$1', 2, '$M', '$AC', '$P1', 'C', $3, 'USD')"
}
txn() { # id, source event
  echo "insert into ledger_transactions (id, merchant_id, payment_id, kind, source_event_id, effective_at) values ('$1', '$M', '$P1', 'capture', '$2', now())"
}
accepts "balanced ledger transaction" "begin; $(txn "$L1" "$L1"); $(entries "$L1" 100 100); commit;"
rejects "same business event twice" "$TEST_URL" "ledger_transactions_.*pkey\|duplicate key" "$(txn "$L1" "$L1")"
rejects "transaction ID not derived from its event" "$TEST_URL" "ledger_transactions_id_is_source_event" "$(txn "$L2" "$L1")"
rejects "unbalanced ledger transaction" "$TEST_URL" "unbalanced ledger transaction" "begin; $(txn "$L2" "$L2"); $(entries "$L2" 100 90); commit;"
rejects "app can't update ledger entries" "$APP_URL" "permission denied" "update ledger_entries set amount_minor = 1"
rejects "app can't delete ledger transactions" "$APP_URL" "permission denied" "delete from ledger_transactions"
rejects "app can't update ledger transactions" "$APP_URL" "permission denied" "update ledger_transactions set kind = 'x'"

# Payments and refunds
rejects "refunds above captured" "$TEST_URL" "check constraint" "update payments set refunded_minor = 1 where id = '$P1'"
rejects "refund for a missing payment" "$TEST_URL" "foreign key" "insert into refunds (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id) values ('$L2', '$L2', '$M', 1, 'PENDING', 'mock', '$PA')"
accepts "idempotency key stored" "insert into idempotency_keys (merchant_id, key, request_hash) values ('$M', 'k1', '\\x01')"
rejects "same idempotency key twice" "$TEST_URL" "duplicate key" "insert into idempotency_keys (merchant_id, key, request_hash) values ('$M', 'k1', '\\x02')"

# Settlement
line() { # line number, payment id, attempt id
  echo "insert into settlement_lines (psp, psp_account_id, file_id, line_no, kind, amount_minor, currency, state, payment_id, attempt_id)
        values ('mock', '$PA', 'f1', $1, 'capture', 100, 'USD', 'MATCHED', '$2', '$3')"
}
accepts "matched settlement line" "$(line 1 "$P1" "$A1")"
rejects "repeated settlement line" "$TEST_URL" "settlement_lines_one_attempt" "$(line 2 "$P1" "$A1")"
rejects "settlement line for another payment's attempt" "$TEST_URL" "foreign key" "$(line 3 "$P1" "$A5")"
rejects "attempt set without its payment" "$TEST_URL" "settlement_lines_attempt_has_payment" "insert into settlement_lines (psp, psp_account_id, file_id, line_no, kind, amount_minor, currency, state, attempt_id) values ('mock', '$PA', 'f1', 4, 'capture', 100, 'USD', 'UNMATCHED', '$A1')"

# Partitions and spike leftovers
parts=$(run "$TEST_URL" "select count(*) from pg_inherits where inhparent in ('payments'::regclass, 'payment_attempts'::regclass, 'ledger_transactions'::regclass, 'outbox_default'::regclass)")
[ "$parts" = 128 ] && pass "32 partitions on each hot table" || fail "32 partitions on each hot table: $parts"
spike=$(run "$TEST_URL" "select count(*) from pg_class where relname like 'spike%'")
[ "$spike" = 0 ] && pass "no spike tables" || fail "no spike tables: $spike"
if out=$(psql "$TEST_URL" -XAtq -v ON_ERROR_STOP=1 -f "$REPO/db/spike/verify.sql" 2>&1); then pass "spike verification passes"; else fail "spike verification passes: $out"; fi

[ "$failed" = 0 ] && echo "Migration check passed" || echo "Migration check FAILED"
exit "$failed"
