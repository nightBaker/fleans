# 45 — Redis Streaming Provider (default)

## Scenario
Verify that the **default** Orleans stream provider (Redis, as of v0.3.0) durably
carries workflow events across silo restarts. Run a chained-script-task workflow,
kill the silo between the first and second task, and confirm the second task still
runs to completion after restart. This is the at-least-once delivery guarantee
that the new default buys us — Memory streams (the old default) would lose the
second task entirely.

Uses the third-party `Universley.OrleansContrib.StreamsProvider.Redis` package,
wired through `FleanStreamingExtensions.AddFleanStreaming` and aliased to the
existing `orleans-redis` Aspire-managed Redis container.

## Prerequisites
- Aspire stack started **with the new default** (no env override needed):
  ```
  dotnet run --project Fleans.Aspire
  ```
- Fresh dev DB (`fleans-dev.db` deleted, or a new `FLEANS_SQLITE_CONNECTION`).
- The Aspire dashboard shows an `orleans-redis` resource running.

## Steps

### 1. Verify Redis streaming is wired
- Open the Aspire dashboard.
- [ ] Resources tab shows `orleans-redis` running (the same Redis container the
      engine already uses for clustering + `PubSubStore`).
- [ ] `fleans-core` env tab includes `Fleans__Streaming__Provider=Redis`.
- [ ] No `fleans-kafka` or `fleans-azurite` resources are running (those are
      opt-in via `FLEANS_STREAMING_PROVIDER=Kafka` / `=AzureQueue`).

### 2. Deploy the workflow
- Navigate to the Workflows page in the Web UI (`https://localhost:7124`).
- Click "Create New", import `redis-streams.bpmn`.
- Click Deploy, confirm `redis-streams` v1 is registered.

### 3. Start an instance, kill the silo between tasks
- Click "Start" on `redis-streams`. Note the new instance id.
- Immediately kill the `fleans-core` silo from the Aspire dashboard
  (Stop button) **before** the second script task runs.
- Wait until the dashboard shows `fleans-core` stopped.
- Restart `fleans-core` from the Aspire dashboard.

### 4. Verify outcome
- Navigate to the Instance Viewer for the started instance.
- [ ] Instance status: **Completed**.
- [ ] Activities tab: `firstTask`, `secondTask`, `start`, `end` all completed.
- [ ] Variables tab: `first` = `"done"` and `second` = `"done"`.
- [ ] No failed activities.
- [ ] Instance logs show no unhandled exceptions during the silo restart.

### 5. Verify Redis-side state
- Connect to the `orleans-redis` container (`docker exec -it <container> redis-cli`).
- [ ] `KEYS '*StreamProvider*'` returns at least one Redis Streams key.
- [ ] `XLEN <key>` on one of those keys returns >= 2 (firstTask + secondTask deliveries).
- [ ] `XINFO GROUPS <key>` shows a consumer group with `pending = 0` after recovery
      (messages were ACKed; the consumer-group reclaim path drained them).

### 6. Verify opt-out still works
- Stop Aspire. Restart with `FLEANS_STREAMING_PROVIDER=Memory dotnet run --project Fleans.Aspire`.
- [ ] `fleans-core` env tab includes `Fleans__Streaming__Provider=Memory`.
- [ ] A simple workflow still runs end-to-end (single-silo Memory streams suffice
      when nothing is killed mid-flight).
- [ ] Killing the silo mid-stream in this mode would **lose** the second task —
      the whole point of Redis being the new default.

## Notes
- The Redis stream provider name is `StreamProvider` (matches Kafka/AzureQueue).
- Default `TotalQueueCount` is `8` (Orleans `HashRingStreamQueueMapperOptions`),
  so up to 8 Redis Streams keys may exist for the provider.
- `MaxStreamLength=1000` (in `RedisStreamReceiverOptions`) trims older entries
  automatically — operators on long-running deployments don't need to manually
  prune.
- Redis persistence (AOF or RDB) **must** be enabled in production. The Aspire
  Redis container defaults to no persistence — fine for dev, NOT for production
  helm chart deployments. The chart `values.yaml` should set persistence appropriately.
- The third-party `Universley.OrleansContrib.StreamsProvider.Redis` package uses
  date-based versioning (`YYYY.M.D`). Pinned at `2026.4.19` in
  `Fleans.ServiceDefaults.csproj` — bump deliberately and re-run this test.
