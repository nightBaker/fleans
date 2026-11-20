---
title: JavaScript Script Tasks
description: Run scriptFormat="javascript" script tasks on the JavaScript worker plugin from fleans-plugins, isolated from the engine.
---

Fleans runs C# script tasks itself. A script task in any other format runs on an
[external worker](/fleans/guides/external-workers/) registered for `fleans.script.<format>` — see
[other script languages](/fleans/concepts/activities/tasks/#other-script-languages). For JavaScript,
that worker is the **JavaScript plugin** (`Fleans.Plugins.JavaScript`) from
[fleans-plugins](https://github.com/nightBaker/fleans-plugins). It runs scripts on
[Jint](https://github.com/sebastienros/jint), an interpreter with no access to .NET, files or the
network. A runaway, memory-hungry or malicious script hits the plugin's limits in the plugin's
own process, not in the engine.

The plugin needs an engine release that runs script formats on workers (the first release after
0.14.0). On older engines, deploying `scriptFormat="javascript"` fails.

## 1. Write the script task

```xml
<bpmn:scriptTask id="compute" name="Compute total" scriptFormat="javascript">
  <bpmn:script><![CDATA[
    function square(n) { return n * n; }
    let total = 0;
    for (const n of _context.items) { total += square(n); }
    _context.total = total;
    _context.label = `order-${_context.orderId}`;
  ]]></bpmn:script>
</bpmn:scriptTask>
```

`js` works as the format too. `_context` is a plain object holding a JSON copy of every variable
the task can see. Only the top-level variables the script **changes** go back and merge into the
scope; the others keep their value and type. Deleting a key does not remove the variable.

| Limit | Value |
|---|---|
| Run time | 10 s |
| Allocations | 64 MB |
| Call depth | 256 |

A script that throws, or goes over a limit, fails the task with code **`500`** and the error's
message — catch it with an [error boundary](/fleans/guides/error-handling/). A syntax error shows
up when the task runs, not at deploy.

## 2. Run the worker

The plugin is in the `ghcr.io/nightbaker/fleans-plugins` image. Run it as **its own deployment**
with `Plugins__Enabled=javascript`: it then loads only this plugin, needs no database, and only
connects out to the Fleans API.

With the [Docker Compose bundle](/fleans/guides/self-host-docker-compose/), start the engine first
with `COMPOSE_PROJECT_NAME=fleans` in `.env`, then add a `docker-compose.override.yaml`:

```yaml
services:
  fleans-javascript:
    image: ghcr.io/nightbaker/fleans-plugins:latest
    networks: [fleans_default]
    environment:
      Plugins__Enabled: "javascript"
      Fleans__ExternalTaskWorker__EngineUrl: "http://fleans-core:8080"
    user: "1654:1654"
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    cpus: 1
    mem_limit: 512m
    depends_on: [fleans-core]
networks:
  fleans_default:
    external: true
    name: fleans_default
```

On Kubernetes, run the image as a non-root Deployment with a read-only root filesystem, CPU and
memory limits, and a NetworkPolicy that allows egress only to the Fleans API (and DNS). The
[plugin guide](https://github.com/nightBaker/fleans-plugins/blob/main/docs/javascript/README.md#kubernetes)
has the full manifest.

Once the worker connects, `GET /custom-tasks/fleans.script.javascript` lists it. It is not offered
in the editor's service-task plugin picker.

## 3. Deploy

Deploy as usual. If no JavaScript worker is connected, the deploy still succeeds and shows a
**warning** naming the script task and `fleans.script.javascript`. Instances wait on that task until a
worker connects.

## Trade-offs

- Each run is an external-task round trip, tens of milliseconds, instead of an in-process
  call of a few milliseconds. Keep hot loops of tiny steps in C#.
- One more component to deploy. In return, scripts are isolated, the worker scales on its own, and you
  upgrade the JavaScript runtime without upgrading the engine.
- Delivery is at-least-once: after a dropped connection a script can run again. Script
  tasks only compute variables, so this is harmless unless the script itself has side effects (it cannot
  reach the network).
