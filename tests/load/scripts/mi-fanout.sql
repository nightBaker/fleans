-- tests/load/scripts/mi-fanout.sql — journal and snapshot size of the instances of a mi-fanout run (#861).
-- Pass the instance ids that scripts/mi-fanout.js printed:
--   docker compose exec -T citus-coord psql -U postgres -d journal \
--     -v ids="'{id1,id2}'" < scripts/mi-fanout.sql
SELECT b."InstanceId",
       count(*)                 AS batches,
       max(b."LastVersion")     AS events,
       sum(length(b."Payload")) AS journal_bytes,
       (SELECT length(s."Payload") FROM "WorkflowSnapshots" s WHERE s."InstanceId" = b."InstanceId") AS snapshot_bytes
FROM "WorkflowEventBatches" b
WHERE b."InstanceId" = ANY(:ids::uuid[])
GROUP BY b."InstanceId"
ORDER BY events;
