# 83 — Script formats on external workers (JavaScript plugin)

**Feature:** a `<scriptTask>` in any format other than C# runs on the external-task worker of
`fleans.script.<format>` (#1105); `javascript` / `js` is served by `Fleans.Plugins.JavaScript` from
[fleans-plugins](https://github.com/nightBaker/fleans-plugins). C# scripts are unaffected.

**Automated coverage (not repeated here):** E2E `ScriptFormatWorkerTests` (fixtures in this folder,
served by the test format `e2e-json` of `Fleans.E2E.ExternalWorker`: completion and merge, failure
with `500` and the message, deploy warning); Application `ExternalScriptTaskTests` (gRPC worker,
BPMN error code to an error boundary, deploy warning); Domain `ScriptTaskActivityTests` (routing,
aliases); fleans-plugins `JavaScriptScriptEngineTests` / `JavaScriptScriptHandlerTests` (Jint
engine, limits, the fixture's script); E2E `JavaScriptScriptTaskTests` runs scenarios 2.4 and 3
against the real plugin (`FLEANS_JAVASCRIPT_WORKER_PROJECT`). What is left: the deploy-then-connect
flow, the editor hint and picker, and the timeout.

## Prerequisites

- Aspire stack from `src/Fleans/`: `dotnet run --project Fleans.Aspire`. API origin `https://localhost:7140`.
- A checkout of `nightBaker/fleans-plugins`. Its worker, JavaScript only:
  `Plugins__Enabled=javascript Fleans__ExternalTaskWorker__EngineUrl=https://localhost:7140 dotnet run --project src/Fleans.Plugins.Host`
  (or the image with the same two variables).
- Fixtures: `../02-script-tasks/javascript-variables.bpmn`, `../02-script-tasks/javascript-throws.bpmn`.

## Scenario 1 — Deploy without a worker warns, the task waits

1. With the plugin worker **stopped**, deploy `javascript-variables.bpmn` in the editor.
   Expect a successful deploy with a warning naming task `compute` and `fleans.script.javascript`.
2. Start `javascript-variables` — the instance stays Active on `compute`.
3. Start the plugin worker within the stream delivery window (6 min). The instance completes.

## Scenario 2 — A JavaScript task completes and writes back what changed

1. With the worker running, `curl -k https://localhost:7140/custom-tasks/fleans.script.javascript` → 200,
   `displayName` `JavaScript script tasks`, one worker name.
2. In the editor, select a service task: the plugin picker does **not** offer `fleans.script.javascript`.
3. Select the `compute` script task: under *Script Format* the hint names `fleans.script.javascript`;
   it disappears for `csharp`.
4. Deploy (no warning) and start `javascript-variables`. Expect completed, `x` = `24`,
   `greeting` = `hello 24`.

## Scenario 3 — A throwing script fails with 500 and its message

1. Deploy and start `javascript-throws`. Expect the instance failed; `compute` has error code `500`
   and a message containing `x is too large: 10`; `end` never runs.

## Scenario 4 — Isolation

1. Start an instance of a process whose JavaScript script is `while (true) {}`. After 10 s the task
   fails with `500` (timeout); the engine's silos stay responsive throughout.
2. Stop the worker while a task runs: the task is delivered again when it reconnects.
