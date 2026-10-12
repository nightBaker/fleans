---
title: Migrating Camunda 7 External Tasks
description: Keep your Camunda 7 external-task workers unchanged — the Fleans.Plugins.CamundaExternalTask plugin serves the Camunda /external-task REST API on top of Fleans.
---

Camunda 7 **external tasks** let workers *pull* work over REST: a worker calls
`fetchAndLock` for a topic, does the job, then calls `complete`, `failure` or `bpmnError`.
The **`Fleans.Plugins.CamundaExternalTask`** NuGet package provides the same REST API on
top of Fleans, so you can move your processes to Fleans and keep your workers as they are.
In most cases you only change the worker's base URL.

The engine itself has no Camunda-specific code. The plugin is an ordinary
[custom-task plugin](/fleans/concepts/custom-tasks/) running in your own
[plugin host](/fleans/guides/custom-worker-host/):

1. A service task with `type="camunda:<topic>"` publishes a custom-task event, as any custom task does.
2. The plugin receives the event and adds a row to its own `camunda_external_tasks` table.
3. Workers fetch, lock, complete or fail rows through `/engine-rest/external-task/*`. The plugin
   reports each result back to the workflow, either completing the activity with the worker's
   variables or failing it.

## 1. Adapt the BPMN

Replace Camunda's `camunda:type="external" camunda:topic="…"` attribute pair with a Fleans
task type that starts with `camunda:`:

```xml
<!-- Camunda 7 -->
<bpmn:serviceTask id="charge" camunda:type="external" camunda:topic="charge-card" />

<!-- Fleans -->
<bpmn:serviceTask id="charge" type="camunda:charge-card" />
```

The topic is everything after `camunda:`. Workers keep subscribing to `charge-card`.

For most models you can do the rewrite mechanically. Check the diff afterwards:

```bash
sed -E -i.bak \
  's/camunda:type="external"[[:space:]]+camunda:topic="([^"]+)"/type="camunda:\1"/g' \
  processes/*.bpmn
```

Optionally, map variables with [Fleans input/output mappings](/fleans/concepts/custom-tasks/#mapping-grammar)
(`<zeebe:ioMapping>`). Input mappings appear to the worker as extra (local) variables. Output
mappings read the variables the worker sends on `complete`, including `localVariables`.
Camunda 7's `<camunda:inputOutput>` is **not** read, so convert those mappings by hand.

## 2. Host the plugin

The plugin runs in a [custom plugin host](/fleans/guides/custom-worker-host/) (`Fleans:Role=Plugin`)
that joins the engine's Orleans cluster. The same process serves the REST API. Add the
packages:

```bash
dotnet add package Fleans.Plugins.CamundaExternalTask
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # or Microsoft.EntityFrameworkCore.Sqlite
```

Then register the plugin and map the endpoints in `Program.cs`:

```csharp
using Fleans.Plugins.CamundaExternalTask;
using Fleans.Worker.Hosting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.UseOrleans(silo =>
{
    silo.AddFleansPluginHost(builder.Configuration);
    // … clustering, streaming and reminders exactly as in the plugin-host template
});

builder.Services.AddCamundaExternalTasks(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("camunda-external-tasks")));

var app = builder.Build();
app.MapCamundaExternalTaskApi();          // → /engine-rest/external-task/*
app.Run();
```

- **Storage.** The table is created at startup if it is missing (`CREATE TABLE IF NOT EXISTS`),
  in a dedicated database or in an existing one. PostgreSQL and SQLite are supported. Use
  PostgreSQL when the API runs on more than one replica: locks are claimed with a conditional
  `UPDATE`, so replicas can share the table. Lock expiry is computed from each replica's own
  clock, so keep replica clocks in sync (NTP). A skew larger than a lock duration lets
  another replica take over a task that is still locked.
- **Base path.** `MapCamundaExternalTaskApi("/my-prefix")` changes the `/engine-rest` prefix.
- **Authentication.** The endpoints are anonymous by default. The method returns the route
  group, so add your own policy, for example `app.MapCamundaExternalTaskApi().RequireAuthorization();`.

## 3. Point the workers at Fleans

Change only the base URL:

```java
ExternalTaskClient client = ExternalTaskClient.create()
    .baseUrl("http://camunda-plugin-host:8080/engine-rest")
    .build();
```

```js
const client = new Client({ baseUrl: "http://camunda-plugin-host:8080/engine-rest" });
```

## Supported endpoints

| Endpoint | Notes |
|---|---|
| `POST /external-task/fetchAndLock` | `workerId`, `maxTasks`, `usePriority`, `asyncResponseTimeout` (long polling, up to 30 min), per topic `topicName`, `lockDuration`, `variables`, `localVariables`, `processDefinitionId(In)`, `processDefinitionKey(In)`. |
| `POST /external-task/{id}/complete` | `variables` are written to the workflow scope. `localVariables` are visible only to output mappings. |
| `POST /external-task/{id}/failure` | `retries > 0` makes the task fetchable again after `retryTimeout`. `retries = 0` fails the activity (see below). |
| `POST /external-task/{id}/bpmnError` | Fails the activity with `errorCode`. A matching [error boundary event](/fleans/guides/error-handling/) catches it. |
| `POST /external-task/{id}/extendLock`, `/lock`, `/unlock` | Same semantics as Camunda. |
| `PUT /external-task/{id}/retries`, `/priority` | Setting retries to `0` fails the activity. |
| `GET /external-task`, `/count`, `/{id}`, `/topic-names` | Filters: `topicName`, `workerId`, `locked`, `notLocked`, `withRetriesLeft`, `noRetriesLeft`, `processInstanceId`, `activityId`, `processDefinitionId`, `processDefinitionKey`, `firstResult`, `maxResults`. |

Errors use Camunda's shape, `{ "type": "…", "message": "…" }`, with Camunda's status codes:
`404` for an unknown task, `500` when the task is locked by another worker, and `400` for an
invalid request.

## Differences from Camunda 7

- **No incidents.** When a worker reports `failure` with `retries = 0`, Camunda creates an
  incident and leaves the token waiting. Fleans fails the activity instead, with error code
  `500` and the worker's `errorMessage` (plus `errorDetails`). The failure appears in the
  Fleans UI, and an error boundary event can catch it.
- **`bpmnError` variables are ignored.** The error is raised, but variables sent with it are
  not written (a warning is logged). If the error path needs data, send it with an earlier `complete` or
  `failure`.
- **Variable types.** Supported: `String`, `Boolean`, `Integer`, `Short`, `Long`, `Double`,
  `Date` (kept as a string), `Null`, `Json`, and `Object` with
  `serializationDataFormat: application/json`. Workers receive objects and lists as `Json`.
  Whole numbers come back as `Integer` when they fit in 32 bits and as `Long` otherwise,
  whatever type the worker originally sent. Java workers should read numbers as `Number`,
  not cast them to `Long`.
  `Bytes`, `File` and Java-serialized objects are rejected with `400`.
- **Identifiers.** Task `id`, `executionId` and `activityInstanceId` are the Fleans activity
  instance id. `processInstanceId` is the workflow instance id, and `processDefinitionKey` is the
  process id. `businessKey`, `tenantId` and `processDefinitionVersionTag` are always `null`.
  Fetch filters on them are ignored.
- **Stale tasks.** If the activity ends without a worker (cancelled, or interrupted by a boundary
  event), its row is removed the next time a worker fetches from that topic. Until then,
  `GET /external-task` still lists it.
- **Scope.** Only the `/external-task` resource is provided. Start processes, correlate messages
  and query instances through the [Fleans REST API](/fleans/reference/api/).
- **Catalog.** Topics are open-ended, so `camunda:*` task types are not registered in the
  custom-task catalog (`GET /custom-tasks`).

## Logs

The plugin logs with EventIds 9350–9367, for example 9350 *queued*, 9360 *locked*,
9361 *completed*, 9362 *failure with retries*, 9363 *retries exhausted* and 9364 *BPMN error*.
Filter on the `ExternalTaskId` or `TopicName` fields.
