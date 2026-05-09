# 44 — Azure Queue Storage Streaming Provider

## Scenario
Switch the Orleans stream provider from in-memory (default) to Azure Queue Storage via
`FLEANS_STREAMING_PROVIDER=AzureQueue`, run a chained-script-task workflow, kill the
silo between the first and second task, and verify the second task still runs to
completion after restart. Verifies cross-silo event delivery via Azure Queue Storage
(Azurite emulator in local dev).

## Prerequisites
- Aspire stack started **with Azure Queue enabled**:
  ```
  FLEANS_STREAMING_PROVIDER=AzureQueue dotnet run --project Fleans.Aspire
  ```
- Fresh dev DB (`fleans-dev.db` deleted, or a new `FLEANS_SQLITE_CONNECTION`).
- First run will pull the Azurite emulator image (~200 MB) — allow extra startup time.
- The Aspire dashboard shows a `fleans-azurite` resource.

## Steps

### 1. Verify Azurite is provisioned
- Open the Aspire dashboard.
- [ ] Resources tab shows `fleans-azurite` running.
- [ ] `fleans-core` env tab includes
      `Fleans__Streaming__Provider=AzureQueue` and a non-empty
      `Fleans__Streaming__AzureQueue__ConnectionString`.

### 2. Deploy the workflow
- Navigate to the Workflows page in the Web UI (`https://localhost:7124`).
- Click "Create New", import `azure-queue-streams.bpmn`.
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

### 5. Verify Azure queues were created
- In the Aspire dashboard, find the `fleans-azurite` resource's connection string.
- Use Azure Storage Explorer (or `az storage queue list`) to list queues:
  ```
  az storage queue list --connection-string "<connection-string>"
  ```
- [ ] 8 queues named `fleans-stream-0` … `fleans-stream-7` exist.
- [ ] At least one queue has a non-zero message count during workflow execution.

## Notes
- Default `AzureQueueStreamingOptions.QueueNames` is 8 queues
  (`fleans-stream-0` … `fleans-stream-7`). Orleans distributes grain stream
  subscriptions across these queues.
- In production, set `Fleans:Streaming:AzureQueue:AccountName` instead of
  `ConnectionString` to use Managed Identity. See `reference/streaming.md`.
- Subscription state lives in Redis (`PubSubStore`) — Azure Queue adds event
  durability, not subscription durability.
- `MessageVisibilityTimeout` defaults to the Azure SDK default (30 s). Increase
  if workflows take longer than 30 s between steps under load.
