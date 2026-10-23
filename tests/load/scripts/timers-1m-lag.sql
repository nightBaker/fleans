-- tests/load/scripts/timers-1m-lag.sql — results of a timers-1m run (#886).
-- Run against the `fleans` database once the due minute has passed:
--   docker compose exec -T postgres psql -U postgres -d fleans < scripts/timers-1m-lag.sql
--
-- Each load-timers-NN definition carries its due time in the BPMN timeDate; an instance's
-- CompletedAt (read model, from the event's OccurredAt) minus that due time is its timer lag,
-- plus one start → end transition. Not fired yet = armed but CompletedAt still NULL.
WITH due AS (
    SELECT pd."ProcessDefinitionId",
           substring(pd."BpmnXml" from '<timeDate>([^<]+)</timeDate>')::timestamptz AS due_at
    FROM "ProcessDefinitions" pd
    WHERE pd."ProcessDefinitionKey" LIKE 'load-timers-%'
),
lag AS (
    SELECT EXTRACT(EPOCH FROM (wi."CompletedAt" - due.due_at)) AS lag_s,
           wi."IsCompleted", wi."IsFailed"
    FROM "WorkflowInstances" wi
    JOIN due USING ("ProcessDefinitionId")
)
SELECT count(*)                                                                    AS armed,
       count(*) FILTER (WHERE "IsCompleted")                                       AS fired,
       count(*) FILTER (WHERE "IsFailed")                                          AS failed,
       count(*) FILTER (WHERE NOT "IsCompleted" AND NOT "IsFailed")                AS not_fired,
       round(percentile_cont(0.50) WITHIN GROUP (ORDER BY lag_s)::numeric, 3)     AS lag_p50_s,
       round(percentile_cont(0.99) WITHIN GROUP (ORDER BY lag_s)::numeric, 3)     AS lag_p99_s,
       round(max(lag_s)::numeric, 3)                                               AS lag_max_s
FROM lag;
