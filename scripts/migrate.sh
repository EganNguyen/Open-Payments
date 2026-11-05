#!/usr/bin/env bash
# Applies db/migrations/*.sql in name order, each once and in its own transaction, recording its
# version and checksum. Stops if an applied migration has changed: write a new one instead.
# Runs as the admin role, so the application role doesn't own its own tables.
# Environment: DB_ADMIN_URL, MIGRATIONS_DIR.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
ADMIN=${DB_ADMIN_URL:-postgres://postgres:op_local_only@127.0.0.1:15432/openpayments}
DIR=${MIGRATIONS_DIR:-$REPO/db/migrations}

query() { psql "$ADMIN" -XAtq -v ON_ERROR_STOP=1 "$@"; }

query -c "create table if not exists schema_migrations (
  version text primary key, checksum text not null, applied_at timestamptz not null default now())"

for file in "$DIR"/*.sql; do
  version=$(basename "$file" .sql)
  checksum=$(shasum -a 256 "$file" | cut -d' ' -f1)
  recorded=$(query -c "select checksum from schema_migrations where version = '$version'")
  if [ "$recorded" = "$checksum" ]; then
    echo "skip   $version"
  elif [ -n "$recorded" ]; then
    echo "$version has changed since it was applied. Write a new migration instead." >&2
    exit 1
  else
    query --single-transaction -f "$file" \
      -c "insert into schema_migrations (version, checksum) values ('$version', '$checksum')"
    echo "apply  $version"
  fi
done
