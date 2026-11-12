-- tests/load/scripts/long-lived.sql — results of a long-lived run (#973).
-- Run against the `fleans` database once the last linear-10 window has drained:
--   docker compose exec -T postgres psql -U postgres -d fleans < scripts/long-lived.sql
--
-- First row: parked instances and how many still wait (all of them should). Second row set:
-- linear-10 per window — 'empty' started before the first parked instance, 'loaded' after the last
-- one — with starts, completions, completions per second over the window's span and duration p99.
SELECT count(*)                                                             AS parked,
       count(*) FILTER (WHERE NOT wi."IsCompleted" AND NOT wi."IsFailed")   AS waiting,
       count(*) FILTER (WHERE wi."IsFailed")                                AS failed,
       min(wi."CreatedAt")                                                  AS first_parked,
       max(wi."CreatedAt")                                                  AS last_parked
FROM "WorkflowInstances" wi
JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
WHERE pd."ProcessDefinitionKey" = 'load-long-lived';

WITH park AS (
    SELECT min(wi."CreatedAt") AS first_parked, max(wi."CreatedAt") AS last_parked
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    WHERE pd."ProcessDefinitionKey" = 'load-long-lived'
),
linear AS (
    SELECT CASE WHEN wi."CreatedAt" < park.first_parked THEN 'empty'
                WHEN wi."CreatedAt" > park.last_parked  THEN 'loaded' END AS phase,
           wi."CreatedAt", wi."CompletedAt", wi."IsCompleted"
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    CROSS JOIN park
    WHERE pd."ProcessDefinitionKey" = 'load-linear-10'
)
SELECT phase,
       count(*)                                                             AS started,
       count(*) FILTER (WHERE "IsCompleted")                                AS completed,
       round((count(*) FILTER (WHERE "IsCompleted")
              / NULLIF(EXTRACT(EPOCH FROM (max("CreatedAt") - min("CreatedAt"))), 0))::numeric, 1) AS completed_per_s,
       round(percentile_cont(0.99) WITHIN GROUP (
           ORDER BY EXTRACT(EPOCH FROM ("CompletedAt" - "CreatedAt")))::numeric, 3) AS duration_p99_s
FROM linear
WHERE phase IS NOT NULL
GROUP BY phase
ORDER BY phase;
