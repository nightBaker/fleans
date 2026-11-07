# 80 — Custom-task plugin versions

**Feature:** #1051 — a plugin's parameters are its contract, and a task can pin a plugin version.

**Automated coverage (not repeated here):** `CustomTaskVersionTests` (E2E), `GrpcExternalTaskWorkerTests`,
`CustomTaskAccessValidatorTests` and `CustomTaskInputRulesTests` (Application), `CustomTaskVersionsTests`
(Domain). What they don't show is how the panel reads to an author, and a second worker being refused
and then connecting in real processes.

Fixture: `versioned-task.bpmn`. It needs the E2E worker, which announces `e2e-ext-versioned` as version
`1.2.0` with a required integer `delayMs` and an optional list of integers `codes`.

## Prerequisites

The Aspire stack with the worker. A SQLite dev database from before this feature has no `Version`
column: delete it and let the stack create a new one.

```bash
cd src/Fleans
FLEANS_EXTERNAL_WORKER_PROJECT=$PWD/Fleans.E2E.ExternalWorker/Fleans.E2E.ExternalWorker.csproj \
  dotnet run --project Fleans.Aspire
```

## Steps

1. Import `versioned-task.bpmn` in the editor and select `Versioned probe`.
   - [ ] `delayMs` shows `integer`, and `codes` shows `list of integer`.
   - [ ] The version picker offers `latest` and `1.2.0`, with `latest` selected.
2. Type `abc` in the `delayMs` field.
   - [ ] The field says it needs a whole number. `=delay` clears the message.
3. Pick `1.2.0`.
   - [ ] The XML has `version="1.2.0"` on `fleans:taskDefinition`.
4. Deploy, then start an instance with the variable `delay` set to `1`.
   - [ ] The deploy shows no warning, and the instance completes.
5. Change the XML to `version="9.9.9"` and deploy.
   - [ ] The deploy succeeds and warns that no worker of `9.9.9` is connected.
   - [ ] The panel says the version isn't connected.
6. Put the version back, rename the target to `delaysMs`, and deploy.
   - [ ] The panel marks `delaysMs` as undeclared.
   - [ ] The deploy fails, naming `delaysMs` and the missing `delayMs`.
7. Restore the target. Deploy and start an instance with `delay` set to the text `soon`.
   - [ ] The activity fails with `PLUGIN_INPUTS_INVALID`, and the worker's log shows no task.
   - [ ] The error names `delayMs` and the value's type, not `soon`.
8. Stop the worker from the Aspire dashboard. Deploy the unchanged process.
   - [ ] The deploy succeeds and warns that its inputs were not checked.
9. Start the worker again. Temporarily rename `delayMs` in its `Program.cs` and run this changed copy as a
   second process alongside the first, with `Fleans__ExternalTaskWorker__EngineUrl` set to the API's http
   endpoint from the dashboard.
   - [ ] The second worker logs `FailedPrecondition` and keeps retrying, and the API logs EventId 14533.
   - [ ] After the first worker stops, the second connects within `ReconnectDelay`.
   - [ ] Revert the rename.

## Reporting

Record pass or fail per step in the PR or regression run.
