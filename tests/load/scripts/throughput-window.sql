-- tests/load/scripts/throughput-window.sql — the result of one throughput.js run, from the read model.
-- Instances of :'process' created in [:'from', :'to') are the measured window; each one's process
-- duration is CompletedAt − CreatedAt (both the journal events' OccurredAt). Run it after the
-- projector has caught up (throughput-ceiling.sh waits until nothing in the window is running):
--
--   docker compose exec -T postgres psql -U postgres -d fleans -v process=load-linear-10 \
--     -v from='2026-10-24T10:00:00Z' -v to='2026-10-24T10:05:00Z' < scripts/throughput-window.sql
--
-- completed_per_s counts completions per second of the window (the roadmap's start-rate metric);
-- not_finished are instances still running when the query ran — any is a fail for a no-wait process.
WITH w AS (
    SELECT wi."CreatedAt", wi."CompletedAt", wi."IsCompleted", wi."IsFailed",
           EXTRACT(EPOCH FROM (wi."CompletedAt" - wi."CreatedAt")) AS duration_s
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    WHERE pd."ProcessDefinitionKey" = :'process'
      AND wi."CreatedAt" >= :'from'::timestamptz AND wi."CreatedAt" < :'to'::timestamptz
)
SELECT count(*)                                                                    AS started,
       count(*) FILTER (WHERE "IsCompleted" AND NOT "IsFailed")                    AS completed,
       count(*) FILTER (WHERE "IsFailed")                                          AS failed,
       count(*) FILTER (WHERE NOT "IsCompleted" AND NOT "IsFailed")                AS not_finished,
       round((count(*) FILTER (WHERE "IsCompleted" AND NOT "IsFailed")
              / EXTRACT(EPOCH FROM (:'to'::timestamptz - :'from'::timestamptz)))::numeric, 1) AS completed_per_s,
       round(percentile_cont(0.50) WITHIN GROUP (ORDER BY duration_s)::numeric, 3) AS duration_p50_s,
       round(percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_s)::numeric, 3) AS duration_p95_s,
       round(percentile_cont(0.99) WITHIN GROUP (ORDER BY duration_s)::numeric, 3) AS duration_p99_s,
       round(max(duration_s)::numeric, 3)                                          AS duration_max_s
FROM w;
