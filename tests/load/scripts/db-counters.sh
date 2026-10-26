#!/usr/bin/env bash
# db-counters.sh — one line per database server: committed transactions and WAL bytes so far.
# throughput-ceiling.sh takes it before and after the measured window; the difference divided by the
# completed instances is the per-instance database cost (transactions and WAL bytes per instance).
#
#   bash scripts/db-counters.sh        # from tests/load
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

counters() {
  local service="$1" db="$2"
  docker compose exec -T "$service" psql -U postgres -d "$db" -At -F ' ' -c \
    "SELECT '$service', (SELECT sum(xact_commit) FROM pg_stat_database), (SELECT wal_bytes FROM pg_stat_wal)" 2>/dev/null \
    || echo "$service 0 0"
}

counters postgres     fleans
counters citus-coord  journal
counters citus-w1     journal
counters citus-w2     journal
counters postgres-ops ops
