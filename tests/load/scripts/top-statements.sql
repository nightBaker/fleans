-- tests/load/scripts/top-statements.sql — the statements behind a run's per-instance transactions.
--   docker compose exec -T postgres psql -U postgres -d fleans < scripts/top-statements.sql
--   (also postgres-ops / ops). Reset before a run with: SELECT pg_stat_statements_reset();
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SELECT calls, round(total_exec_time::numeric, 0) AS total_ms, rows,
       left(regexp_replace(query, '\s+', ' ', 'g'), 140) AS query
FROM pg_stat_statements
WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
ORDER BY calls DESC
LIMIT 25;
