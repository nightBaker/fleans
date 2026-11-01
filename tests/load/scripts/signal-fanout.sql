-- tests/load/scripts/signal-fanout.sql — results of a signal-fanout run (#976).
-- Run against the `fleans` database once the broadcast has drained:
--   docker compose exec -T postgres psql -U postgres -d fleans < scripts/signal-fanout.sql
--
-- received = parked instances that completed (they received the signal); waiting = parked but
-- not completed. delivery_spread_s is first → last completion; add the broadcast's own answer
-- time from the k6 log for the time to the last instance. The second row set is linear-10:
-- completions per second in the window before and during the broadcast.
SELECT count(*)                                                             AS parked,
       count(*) FILTER (WHERE wi."IsCompleted")                             AS received,
       count(*) FILTER (WHERE wi."IsFailed")                                AS failed,
       count(*) FILTER (WHERE NOT wi."IsCompleted" AND NOT wi."IsFailed")   AS waiting,
       min(wi."CompletedAt")                                                AS first_received,
       max(wi."CompletedAt")                                                AS last_received,
       round(EXTRACT(EPOCH FROM (max(wi."CompletedAt") - min(wi."CompletedAt")))::numeric, 3) AS delivery_spread_s
FROM "WorkflowInstances" wi
JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
WHERE pd."ProcessDefinitionKey" = 'load-signal-fanout';

WITH fanout AS (
    SELECT min(wi."CompletedAt") AS broadcast_at
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    WHERE pd."ProcessDefinitionKey" = 'load-signal-fanout'
),
linear AS (
    SELECT wi."CreatedAt", wi."CompletedAt", wi."IsCompleted", fanout.broadcast_at
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    CROSS JOIN fanout
    WHERE pd."ProcessDefinitionKey" = 'load-linear-10'
)
SELECT CASE WHEN "CreatedAt" < broadcast_at THEN 'before' ELSE 'during' END AS window,
       count(*)                                                             AS started,
       count(*) FILTER (WHERE "IsCompleted")                                AS completed,
       round(percentile_cont(0.99) WITHIN GROUP (
           ORDER BY EXTRACT(EPOCH FROM ("CompletedAt" - "CreatedAt")))::numeric, 3) AS duration_p99_s
FROM linear
GROUP BY 1
ORDER BY 1;
