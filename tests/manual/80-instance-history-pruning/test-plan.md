# 80 — Instance history pruning in the admin UI

**Feature:** #1050 — finished activities leave an instance's snapshots, and the admin UI still shows the instance's full history.

**Automated coverage (not repeated here):** `InstanceHistoryPruningTests`, `InstanceHistoryUnprunedTests` (Application) and `WorkflowExecutionPruningTests` (Domain) cover the snapshot size, full history for running and completed instances, reactivation, late callbacks, failed snapshot writes and journals longer than one read. What they cannot show is what the instance page and the debugger render, including the debugger's steps window.

Fixture: `long-loop.bpmn` (process `long-loop`: `init` → `step` × 200 → user task `hold` → end, about 2,000 journal events).

## Steps

1. Run the stack (`dotnet run --project Fleans.Aspire` from `src/Fleans/`), deploy `long-loop.bpmn` and start an instance.
2. Open the instance when it reaches `hold`. **Expect:** the activity list shows 200 completed `step` entries and 200 completed `again` entries, with the header saying "live". The page stays responsive while it refreshes.
3. Click **Debug**. **Expect:** the slider covers every journal event. The steps list shows only the events around the current one, with a note that the list is limited. Moving the slider, previous, next and **Δ only** all work, and the variables at a step early in the loop show its value of `i`.
4. Jump to the last event, then the first. **Expect:** the list follows the current event each time.
5. Leave debug mode and complete `hold`. **Expect:** the instance completes and the header changes to "read model", still with 200 `step` entries. Debug mode still works.
