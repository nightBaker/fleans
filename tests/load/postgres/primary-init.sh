#!/bin/sh
# Lets postgres-replica stream from the read-model primary (pg_basebackup + WAL streaming).
# Runs once, from /docker-entrypoint-initdb.d, when the primary's data directory is created.
set -e
echo "host replication all all scram-sha-256" >> "$PGDATA/pg_hba.conf"
