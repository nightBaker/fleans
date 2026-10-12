---
title: Migrating Camunda 7 External Tasks
description: Keep your Camunda 7 external-task workers unchanged — the fleans-camunda-external-task plugin serves the Camunda /external-task REST API on top of Fleans.
---

Camunda 7 **external tasks** let workers *pull* work over REST: a worker calls `fetchAndLock`
for a topic, does the job, then calls `complete`, `failure` or `bpmnError`. The
**[fleans-camunda-external-task](https://github.com/nightBaker/fleans-camunda-external-task)**
plugin serves the same REST API on top of Fleans. You can move your processes to Fleans and
keep your workers. In most cases you only change their base URL.

The engine has no Camunda-specific code. The plugin is an ordinary
[custom-task plugin](/fleans/concepts/custom-tasks/) running in its own
[plugin host](/fleans/concepts/plugin-hosting/) that joins the engine's cluster. It stores
waiting tasks in its own table and reports workers' results back to the workflow.

Requires **Fleans 0.6.0 or later**.

## 1. Adapt the BPMN

Replace Camunda's `camunda:type="external" camunda:topic="…"` attribute pair with a Fleans
task type that starts with `camunda:`:

```xml
<!-- Camunda 7 -->
<bpmn:serviceTask id="charge" camunda:type="external" camunda:topic="charge-card" />

<!-- Fleans -->
<bpmn:serviceTask id="charge" type="camunda:charge-card" />
```

Workers keep subscribing to the topic (`charge-card`). Camunda's `<camunda:inputOutput>` is not
read. Use Fleans [input/output mappings](/fleans/concepts/custom-tasks/#mapping-grammar) instead.

## 2. Run the plugin host

The plugin repository ships a ready-to-run host image,
`ghcr.io/nightbaker/fleans-camunda-external-task`. With the
[Docker Compose bundle](/fleans/guides/self-host-docker-compose/), add a
`docker-compose.override.yaml` next to the bundle's `docker-compose.yaml`:

```yaml
services:
  camunda-external-task:
    image: ghcr.io/nightbaker/fleans-camunda-external-task:<version>
    networks: [fleans_default]
    ports: ["8090:8080"]
    environment:
      ConnectionStrings__orleans-redis: "redis:6379,password=${ORLEANS_REDIS_PASSWORD}"
      Orleans__ClusterId: "fleans"
      Orleans__ServiceId: "fleans"
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=postgres;Password=${POSTGRES_PASSWORD}"
    depends_on: [redis, postgres]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

`Orleans__ClusterId` and `Orleans__ServiceId` must match the bundle's `CLUSTER_CLUSTER_ID` and
`CLUSTER_SERVICE_ID`. The database and the `camunda_external_tasks` table are created at startup.

To run the plugin inside a plugin host you already have, add the
`Fleans.Plugins.CamundaExternalTask` NuGet package and register it with
`AddCamundaExternalTasks(...)` and `MapCamundaExternalTaskApi()`. See the
[repository README](https://github.com/nightBaker/fleans-camunda-external-task#readme) for
details and every configuration setting.

## 3. Point the workers at the host

```java
ExternalTaskClient.create().baseUrl("http://camunda-external-task:8080/engine-rest").build();
```

```js
new Client({ baseUrl: "http://camunda-external-task:8080/engine-rest" });
```

`fetchAndLock` (including long polling), `complete`, `failure`, `bpmnError`, `extendLock`,
`lock`, `unlock`, `retries`, `priority` and the `GET` queries are supported. Errors use
Camunda's body and status codes.

## Differences from Camunda 7

- **No incidents.** `failure` with `retries = 0` fails the activity, with error code `500` and the
  worker's error message. It shows as failed in Fleans, and an error boundary event can catch it.
- **`bpmnError`** fails the activity with the worker's `errorCode`, so an
  [error boundary event](/fleans/guides/error-handling/) with that code catches it. Variables
  sent with the BPMN error are ignored.
- **Variables:** primitives, `Date` (kept as a string), `Json`, and `Object` with
  `application/json` are supported. `Bytes`, `File` and Java-serialized objects are rejected.
- **Missing fields:** `businessKey`, `tenantId` and `processDefinitionVersionTag` are always
  `null`, because Fleans has no equivalent.
- **Scope:** only the `/external-task` resource is provided. Start processes and correlate
  messages through the [Fleans REST API](/fleans/reference/api/).

The [repository README](https://github.com/nightBaker/fleans-camunda-external-task#differences-from-camunda-7)
lists every difference.
