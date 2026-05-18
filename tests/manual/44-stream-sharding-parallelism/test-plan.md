# 44 — Stream-id sharding parallelism

Verifies that `WorkflowEventsPublisher`'s per-`WorkflowInstanceId` stream-id sharding (#565) actually distributes `CustomTaskHandlerBase` activations across multiple `fleans-worker` silos, **and** that the subscriber-side resume path (`OnActivateAsync` deriving its stream key from `this.GetPrimaryKeyString()` rather than a literal `nameof(...)`) survives silo restart with in-flight workflows (#590).

## Prerequisites

- A multi-worker deployment (`docker-compose` form of this regression):
  ```bash
  cd <release-bundle>
  docker compose up -d --scale fleans-worker=2
  ```
- A custom-task plugin host running (use `fleans-custom-worker-example` if you have it; otherwise build any `CustomTaskHandlerBase` subclass that delays a few seconds in `ExecuteAsync` so multiple activations can be live simultaneously).
- A BPMN fixture with two parallel `serviceTask` branches both invoking the plugin task type — author one in the editor, deploy it as `parallel-custom-tasks`.

## Steps

1. **Snapshot pre-test Redis stats** (informational only — no merge gate):
   ```bash
   docker exec orleans-redis redis-cli INFO commandstats > /tmp/pre.txt
   ```

2. **Start one workflow instance** of `parallel-custom-tasks`:
   ```bash
   curl -X POST 'http://localhost:8081/api/workflow/start' \
        -H 'Content-Type: application/json' \
        -d '{"processDefinitionKey":"parallel-custom-tasks"}'
   ```

3. **Wait for the workflow to complete**. Then collect activation log lines from both workers:
   ```bash
   docker compose logs fleans-worker 2>&1 \
     | grep 'Custom-task handler activated on silo' \
     | sed -E 's/.*silo ([^ ]+).*/\1/' \
     | sort -u
   ```

4. **Assert** that the output contains **≥2 distinct silo names** (e.g. `worker-fleans-worker-1-xxx`, `worker-fleans-worker-2-xxx`). One silo means sharding didn't take effect.

5. **Snapshot post-test Redis stats** for trend tracking:
   ```bash
   docker exec orleans-redis redis-cli INFO commandstats > /tmp/post.txt
   diff /tmp/pre.txt /tmp/post.txt | head -20
   ```
   Eyeball — no formal threshold here at v0.x scale.

6. **Resume after silo restart (#590)** — separate scenario that exercises the subscriber-side
   `OnActivateAsync` resume path against a real silo restart. Start a **fresh** workflow instance
   of `parallel-custom-tasks` and, while it is still in flight, restart only the second worker
   container:
   ```bash
   curl -X POST 'http://localhost:8081/api/workflow/start' \
        -H 'Content-Type: application/json' \
        -d '{"processDefinitionKey":"parallel-custom-tasks"}'
   sleep 1 && docker compose restart fleans-worker-2
   ```
   The `sleep 1 && …` chain ensures the restart fires inside the plugin's `ExecuteAsync` delay
   window (the prerequisite says the plugin delays a few seconds), so at least one parallel-branch
   handler is mid-execution when its silo dies.

   Confirm:
   - The workflow reaches `Completed` within 30s of the restart. Verify via
     `curl http://localhost:8081/api/workflow/instances/{instanceId}/state` — `Status: Completed`.
   - The activation log shows the parallel-branch handler that was on `fleans-worker-2`
     reactivates either on `fleans-worker-1` mid-restart, or on `fleans-worker-2` post-restart.
     Grep the same signature Step 3 uses (`Custom-task handler activated on silo …`,
     `EventId 4035` from `CustomTaskHandlerBase.LogActivated`):
     ```bash
     docker compose logs fleans-worker 2>&1 \
       | grep 'Custom-task handler activated on silo' \
       | tail -10
     ```
     There should be at least one activation line with a post-restart timestamp.

   Pre-#565, this scenario would hang because the reactivated handle subscribed to
   `nameof(ExecuteCustomTaskEvent)` while the publisher published to
   `WorkflowInstanceId.ToString("D")` — `GetAllSubscriptionHandles()` returned empty,
   `OnNextAsync` never wired, and the workflow stalled forever.

   **If the workflow does not complete within 30s**, log this as a `BUG` (not a non-blocking
   `KNOWN BUG`) and file a regression issue — the resume path has regressed and #590's manual
   coverage caught it.

## Pass criteria

- Step 4 reports ≥2 distinct silo names.
- The workflow instance from Step 2 reaches `Completed`.
- The Step 6 fresh workflow reaches `Completed` within 30s of the silo restart, and the
  activation log shows a post-restart activation entry.
- Pre/post `INFO commandstats` snapshots captured (attach to the regression report).

## Failure modes

- **One silo only** → publishers may not be sharding (`WorkflowEventsPublisher.Publish` uses `WorkflowInstanceId.ToString("D")` as the stream key), or subscribers fell back to the deleted `SubscribeAsync` else-branch (`OnActivateAsync` must rebuild stream id from `this.GetPrimaryKeyString()`).
- **No "Custom-task handler activated on silo …" log lines** → the `LogActivated` `[LoggerMessage]` on `CustomTaskHandlerBase` didn't fire; verify the handler is reached and `ILocalSiloDetails` resolved.
- **Step 6 workflow hangs after restart** → the subscriber-side stream-id-trap regression has returned. `OnActivateAsync` in the affected handler grain is querying the wrong `StreamId` (literal / `nameof(...)` / hard-coded Guid) rather than `this.GetPrimaryKeyString()`. See CLAUDE.md *"Subscriber-side stream-id trap"* for the structural invariant and the five handler-grain files it covers.
