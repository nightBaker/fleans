-- tests/load/scripts/throughput.sql — completed instances per second and process duration
-- percentiles of a load run, per process key, from the read model. The roadmap's headline
-- numbers (website/src/content/docs/roadmap/v1.md, "How it is measured") without a metrics
-- pipeline; with one, the same numbers come from fleans.workflow.terminated / fleans.workflow.duration.
--
-- Run against the `fleans` database once the run has drained (the read model is projected
-- asynchronously, but CreatedAt / CompletedAt are the journaled event times, so a late
-- projection does not skew them):
--   docker compose exec -T postgres psql -U postgres -d fleans < scripts/throughput.sql
--
-- Use a FRESH stack per run (`docker compose down -v`): every instance in the database counts.
--   completed_per_s  completed instances / seconds between the first and the last completion
--                    (ramp-up and cool-down included)
--   peak_10s_per_s   the busiest 10-second window, per second (≈ the sustained-stage rate)
--   p50_s / p99_s    process duration (CompletedAt − CreatedAt), completed instances only
WITH done AS (
    SELECT pd."ProcessDefinitionKey" AS process_key,
           wi."CreatedAt", wi."CompletedAt",
           EXTRACT(EPOCH FROM (wi."CompletedAt" - wi."CreatedAt")) AS duration_s
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    WHERE wi."IsCompleted" AND NOT wi."IsFailed" AND NOT wi."IsCancelled"
      AND wi."CompletedAt" IS NOT NULL AND wi."CreatedAt" IS NOT NULL
),
windows AS (
    SELECT process_key, count(*) / 10.0 AS per_s
    FROM done
    GROUP BY process_key, floor(EXTRACT(EPOCH FROM "CompletedAt") / 10)
),
failed AS (
    SELECT pd."ProcessDefinitionKey" AS process_key, count(*) AS failed
    FROM "WorkflowInstances" wi
    JOIN "ProcessDefinitions" pd USING ("ProcessDefinitionId")
    WHERE wi."IsFailed"
    GROUP BY pd."ProcessDefinitionKey"
)
SELECT d.process_key,
       count(*)                                                                         AS completed,
       coalesce(max(f.failed), 0)                                                       AS failed,
       round((count(*) / NULLIF(EXTRACT(EPOCH FROM (max(d."CompletedAt") - min(d."CompletedAt"))), 0))::numeric, 1)
                                                                                        AS completed_per_s,
       (SELECT round(max(w.per_s)::numeric, 1) FROM windows w WHERE w.process_key = d.process_key)
                                                                                        AS peak_10s_per_s,
       round(percentile_cont(0.50) WITHIN GROUP (ORDER BY d.duration_s)::numeric, 3)  AS p50_s,
       round(percentile_cont(0.99) WITHIN GROUP (ORDER BY d.duration_s)::numeric, 3)  AS p99_s,
       round(max(d.duration_s)::numeric, 3)                                             AS max_s
FROM done d
LEFT JOIN failed f USING (process_key)
GROUP BY d.process_key
ORDER BY d.process_key;
