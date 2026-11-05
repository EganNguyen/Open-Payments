#!/usr/bin/env bash
# Runs once, on an empty data directory, while no standby exists yet. Commits here
# must not wait for a standby, or initialization would hang.
set -euo pipefail

export PGOPTIONS="-c synchronous_commit=local"

psql -v ON_ERROR_STOP=1 --username postgres --dbname postgres <<SQL
create role replicator with replication login password '${REPLICATOR_PASSWORD}';
create role op_app with login password '${APP_PASSWORD}';
alter role op_app set lock_timeout = '200ms';
alter role op_app set statement_timeout = '1s';
alter role op_app set idle_in_transaction_session_timeout = '2s';
create database openpayments;
SQL

psql -v ON_ERROR_STOP=1 --username postgres --dbname openpayments <<SQL
grant all on schema public to op_app;
SQL

echo "host replication replicator all scram-sha-256" >> "$PGDATA/pg_hba.conf"
