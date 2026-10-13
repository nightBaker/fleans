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

Plugin 0.7.0 requires **Fleans 0.7.0 or later**.

:::caution[For migration only — prefer native Fleans plugins]
This plugin is a backward-compatibility bridge. It lets existing Camunda 7 workers keep running
while you move your processes to Fleans. For new tasks, and when you rewrite a worker anyway, use
a native [custom-task plugin](/fleans/guides/writing-custom-tasks/). Native plugins run inside the
Fleans cluster, so they need no polling, locks or extra database. They also get typed inputs and
outputs through mappings, appear in the plugin catalog and the editor, and support everything
Fleans offers, unlike the Camunda subset listed below.
:::

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
read. Use Fleans [input/output mappings](/fleans/concepts/custom-tasks/#mapping-grammar) instead;
the [plugin README](https://github.com/nightBaker/fleans-camunda-external-task#1-adapt-the-bpmn)
has a conversion table.

Camunda task metadata comes from **reserved input targets**, evaluated when the task is queued
(except extension properties, evaluated at fetch time) and never sent as variables:

```xml
<zeebe:ioMapping>
  <zeebe:input source="=order.priority" target="taskPriority" />       <!-- camunda:taskPriority -->
  <zeebe:input source="=order.id" target="businessKey" />
  <zeebe:input source="acme" target="tenantId" />
  <zeebe:input source="eu-west" target="extensionProperty_region" />  <!-- camunda:properties -->
</zeebe:ioMapping>
```

## 2. Run the plugin host

The plugin repository ships a ready-to-run host image,
`ghcr.io/nightbaker/fleans-camunda-external-task`. With the
[Docker Compose bundle](/fleans/guides/self-host-docker-compose/):

Start the engine first. The bundle creates the `fleans_default` network that the plugin host
joins, and the network only gets that name when the compose project is called `fleans`:

```bash
cd <unzipped-engine-compose-bundle>
echo "COMPOSE_PROJECT_NAME=fleans" >> .env   # names the network fleans_default
docker compose up -d                          # engine first: creates the network
```

Then add a `docker-compose.override.yaml` next to the bundle's `docker-compose.yaml`:

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

Then run `docker compose up -d` again from the bundle directory. Compose merges the override and
starts the plugin host on the existing network. If you skip the engine-first step or the project
name, it fails with `network fleans_default declared as external, but could not be found`.

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

Every endpoint the official Java and JS clients call is supported: `fetchAndLock` (including
long polling, `usePriority`, `sorting`, and the `businessKey` / tenant filters), `complete`,
`failure`, `bpmnError`, `lock`, `extendLock`, `unlock`, and the file-variable download. The
operator endpoints are there too: task queries and counts, `topic-names`, `PUT /retries` and
`PUT /priority`. Errors use Camunda's body and status codes. Set
`CamundaExternalTasks__BasicAuth__Username` and `__Password` on the host to require HTTP basic
authentication (the clients' `BasicAuthProvider` / `BasicAuthInterceptor`).

## Differences from Camunda 7

- **Incidents are opt-in.** By default, `failure` with `retries = 0` fails the activity with
  error code `500`, and an error boundary event can catch it. With
  `CamundaExternalTasks__Incidents=true` the task stays as an incident, as in Camunda, until
  `PUT /external-task/{id}/retries` resolves it. Incidents are not shown in the Fleans UI.
- **`bpmnError`** fails the activity with the worker's `errorCode`, so an
  [error boundary event](/fleans/guides/error-handling/) with that code catches it. Its variables
  are visible to the boundary or error event sub-process that catches it, but not to an error
  boundary on a parent workflow's call activity.
- **Variables:** primitives, `Date` (kept as a string), `Json`, `Bytes`, `File`, and `Object`
  with `application/json` are supported. Binary values live in the workflow state, so keep them
  small. Java-serialized objects are rejected.
- **Missing fields:** `processDefinitionVersionTag` is always `null`, because Fleans has no
  equivalent.
- **Scope:** the `/external-task` resource plus the file-variable download. Start processes and
  correlate messages through the [Fleans REST API](/fleans/reference/api/).

The [repository README](https://github.com/nightBaker/fleans-camunda-external-task#differences-from-camunda-7)
lists every difference.
