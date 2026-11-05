#!/usr/bin/env bash
# Starts a streaming standby, cloning the primary the first time.
set -euo pipefail

name=$1
mkdir -p "$PGDATA"
chown -R postgres:postgres "$(dirname "$PGDATA")"
chmod 700 "$PGDATA"

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  until pg_isready -h primary -U replicator -q; do sleep 1; done
  PGPASSWORD=$REPLICATOR_PASSWORD gosu postgres pg_basebackup \
    --dbname "host=primary user=replicator application_name=$name" \
    --pgdata "$PGDATA" --wal-method=stream --write-recovery-conf \
    --create-slot --slot "$name" --checkpoint=fast
fi

# A standby must not wait on standbys of its own.
exec gosu postgres postgres -D "$PGDATA" \
  -c config_file=/etc/postgresql/primary.conf \
  -c synchronous_standby_names=''
