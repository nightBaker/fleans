# 73 — Camunda 7 external-task plugin with a real Camunda client

Checks that an existing Camunda 7 worker built on the official `camunda-external-task-client-js`
runs against Fleans with only `baseUrl` changed. The worker talks to the
`Fleans.Plugins.CamundaExternalTask` REST API (`/engine-rest/external-task/*`).

Automated coverage (not repeated here):

- Engine round trip: `Fleans.Application.Tests/CustomTasks/CamundaExternalTaskTests.cs` (complete with variables and input mappings, retries then exhausted retries, `bpmnError` caught by a boundary event, wrong worker, redelivery).
- REST wire format: `Fleans.Plugins.CamundaExternalTask.Tests` (JSON shapes, exact `application/json` content type, status codes, lock expiry, `extendLock`, retry timeout, queries). The PostgreSQL variant runs with `FLEANS_PG_TESTS=1`.

This plan covers what the tests cannot: a real third-party client against a real cluster.

## Prereqs

- Node 18+ and Docker (for Redis via Aspire).
- A web plugin host that runs the plugin. Copy `src/Fleans/Fleans.E2E.PluginHost` to a scratch
  folder and make four changes:
  1. Change the SDK to `Microsoft.NET.Sdk.Web`, and replace `Host.CreateApplicationBuilder(args)` with `WebApplication.CreateBuilder(args)`.
  2. Add a `ProjectReference` to `Fleans.Plugins.CamundaExternalTask` and a `PackageReference` to `Microsoft.EntityFrameworkCore.Sqlite` 10.0.5.
  3. Register the plugin: `builder.Services.AddCamundaExternalTasks(o => o.UseSqlite("Data Source=camunda-tasks.db"));`
  4. Replace `builder.Build().Run();` with `var app = builder.Build(); app.MapCamundaExternalTaskApi(); app.Run();`
- From `src/Fleans/`, start the stack with that host:
  `FLEANS_PLUGIN_HOST_PROJECT=/abs/path/to/host.csproj dotnet run --project Fleans.Aspire`.
  Note the plugin host's HTTP URL from the Aspire dashboard (`fleans-plugin-host`).
- In this folder, run `npm i camunda-external-task-client-js@3`.
- Deploy `camunda-charge.bpmn` (Web UI → *Process Definitions* → *Upload BPMN*).

Start an instance with `{"WorkflowId":"camunda-charge","Variables":{"amount":42}}` for each
scenario (`POST https://localhost:7140/Execution/start`). The `charge` activity stays **Active**.

## Scenario A — complete

1. `BASE_URL=<plugin-host-url>/engine-rest MODE=complete node worker.js`
2. Expect the worker to log `amount = 42 amountToCharge = 42`, then `✓ completed task …`.
3. The instance is **Completed**, `paid` was reached, and its variables contain `charged = true` and `receipt = {"id":"R-1","total":42.5}`.
4. `GET <plugin-host-url>/engine-rest/external-task/count` returns `{"count":0}`.

## Scenario B — BPMN error

1. Start a new instance, then run `MODE=bpmnError node worker.js`.
2. The instance is **Completed** through `declined`, and `charge` has `errorState.code = "PAYMENT_DECLINED"`.

## Scenario C — failure without retries

1. Start a new instance, then run `MODE=failure node worker.js`.
2. The instance is **Failed**, and `charge` has `errorState.code = "500"` with the message `card service down` followed by `HTTP 503`.
3. The plugin host logs show EventId **9363** (*retries exhausted*).

## Scenario D — cancelled instance

1. Start a new instance and cancel it from the Web UI before running any worker.
2. Run `MODE=complete node worker.js`. The worker receives **no** task, and the plugin host logs EventId **9365** (*stale task removed*).
