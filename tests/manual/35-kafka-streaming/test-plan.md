# 35 — Kafka Streaming Provider

## Scenario
Switch the Orleans stream provider from in-memory (default) to Kafka via
`FLEANS_STREAMING_PROVIDER=Kafka`, run a chained-script-task workflow, kill the
silo between the first and second task, and verify the second task still runs to
completion after restart. Verifies the at-least-once delivery contract and the
client-side topic-ensure logic in `Fleans.Streaming.Kafka`.

## Prerequisites
- Aspire stack started **with Kafka enabled**:
  ```
  FLEANS_STREAMING_PROVIDER=Kafka dotnet run --project Fleans.Aspire
  ```
- Fresh dev DB (`fleans-dev.db` deleted, or a new `FLEANS_SQLITE_CONNECTION`).
- The Aspire dashboard shows a `fleans-kafka` resource and a Kafka UI link.

## Steps

### 1. Verify Kafka is provisioned
- Open the Aspire dashboard.
- [ ] Resources tab shows `fleans-kafka` running.
- [ ] `fleans-core` env tab includes
      `Fleans__Streaming__Provider=Kafka` and a non-empty
      `Fleans__Streaming__Kafka__Brokers`.
- [ ] Kafka UI is reachable from the dashboard link.

### 2. Deploy the workflow
- Navigate to the Workflows page in the Web UI (`https://localhost:7140`).
- Click "Create New", import `kafka-streams.bpmn`.
- Click Deploy, confirm `kafka-streams` v1 is registered.

### 3. Start an instance, kill the silo between tasks
- Click "Start" on `kafka-streams`. Note the new instance id.
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

### 5. Verify Kafka topics were auto-created
- Open the Kafka UI from the Aspire dashboard.
- [ ] One or more topics with prefix `fleans-` exist (e.g. `fleans-0`).
- [ ] At least one of those topics has a non-zero offset on its single partition.

## Notes
- The default `Fleans:Streaming:Kafka:QueueCount` is `1`, so a single topic
  named `fleans-0` carries every event in v1.
- v1 ships **plaintext brokers only** — SASL/TLS is the production-Kafka
  follow-up (see issue tracker for "Production Kafka auth").
- Subscription state lives in Redis (`PubSubStore`) — switching to Kafka adds
  *event durability*, not subscription durability.
