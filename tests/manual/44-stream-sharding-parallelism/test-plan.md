# 44 — Stream-id sharding parallelism

Verifies that `WorkflowEventsPublisher`'s per-`WorkflowInstanceId` stream-id sharding (#565) actually distributes `CustomTaskHandlerBase` activations across multiple `fleans-worker` silos.

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

## Pass criteria

- Step 4 reports ≥2 distinct silo names.
- The workflow instance reaches `Completed`.
- Pre/post `INFO commandstats` snapshots captured (attach to the regression report).

## Failure modes

- **One silo only** → publishers may not be sharding (`WorkflowEventsPublisher.Publish` uses `WorkflowInstanceId.ToString("D")` as the stream key), or subscribers fell back to the deleted `SubscribeAsync` else-branch (`OnActivateAsync` must rebuild stream id from `this.GetPrimaryKeyString()`).
- **No "Custom-task handler activated on silo …" log lines** → the `LogActivated` `[LoggerMessage]` on `CustomTaskHandlerBase` didn't fire; verify the handler is reached and `ILocalSiloDetails` resolved.
