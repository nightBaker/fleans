---
title: Quick Start
description: Run Fleans locally from the released Docker Compose bundle in under 5 minutes.
---

This walkthrough brings up the full Fleans stack from a published release, deploys a
sample workflow through the admin UI, and starts an instance via the REST API.

## Prerequisites

- **Docker Engine 24+** with the Compose v2 plugin (`docker compose version`).
- **~2 GB free disk** for images and the Postgres data volume.

No .NET SDK, no source checkout — the bundle pulls signed container images from
`ghcr.io/nightbaker/fleans-*`.

## 1. Download and run the release bundle

The release pipeline attaches `docker-compose-v<VER>.zip` to every GitHub Release.
Grab the latest:

```bash
gh release download v0.3.0 --repo nightBaker/fleans -p 'docker-compose-*.zip'
unzip docker-compose-v0.3.0.zip -d fleans
cd fleans
docker compose up -d
```

Or, without the `gh` CLI:

```bash
curl -LO https://github.com/nightBaker/fleans/releases/download/v0.3.0/docker-compose-v0.3.0.zip
unzip docker-compose-v0.3.0.zip -d fleans
cd fleans
docker compose up -d
```

:::tip[Pick the latest tag]
Substitute the current release tag (see
[Releases](https://github.com/nightBaker/fleans/releases)) for `v0.3.0`. For
production installs and full configuration reference, see [Self-host with Docker
Compose](/fleans/guides/self-host-docker-compose/).
:::

`docker compose ps` should show every service as `running`. The admin UI lands at
[http://localhost:8080](http://localhost:8080) and the REST API at
[http://localhost:8081](http://localhost:8081).

## 2. Deploy a workflow

Fleans deploys workflows through the admin UI, not via a REST endpoint.

1. Open [http://localhost:8080](http://localhost:8080).
2. Navigate to the **Editor** page.
3. Download the sample workflow: [**my-process.bpmn**](/fleans/samples/my-process.bpmn) —
   a minimal workflow with a single script task that sets a `greeting` variable.
4. Import the file, then click **Deploy**.

```xml
<!-- Excerpt from my-process.bpmn -->
<scriptTask id="greet" name="Set Greeting" scriptFormat="csharp">
  <script>_context.greeting = "Hello from Fleans!";</script>
</scriptTask>
```

After clicking **Deploy**, the Editor shows a green success message bar reading
`Deployed 'my-process' v1 (N activities, M flows)`, the breadcrumb displays the
process key, and an accent-colored `v1` badge appears next to it. Deploying again
produces `v2`, and so on.

:::note
Script tasks execute automatically — no external `complete-activity` call is
needed. The engine runs the C# script inline and advances the workflow to the
next element.
:::

## 3. Start an instance

```bash
curl -X POST http://localhost:8081/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"my-process"}'
```

:::note[Expected output]
HTTP `200` with a JSON body like:

```json
{"workflowInstanceId":"<guid>"}
```

The `<guid>` is new for every run.
:::

Because `my-process` contains only a script task, the instance runs to completion
immediately. Verify the result in the admin UI: from the **Workflows** page, click
the `my-process` row to open the process-instances list. The instance appears as
`Completed`, and opening it shows the variables panel containing
`greeting = "Hello from Fleans!"`.

## Where to next

- [Self-host with Docker Compose](/fleans/guides/self-host-docker-compose/) — `.env` reference, secrets, upgrades, troubleshooting.
- [Self-host with Helm](/fleans/guides/self-host-helm/) — Kubernetes install.
- [REST API reference](/fleans/reference/api/) — wire shapes for every endpoint.
- [Service Tasks](/fleans/guides/service-tasks/) — start of the Building Workflows guides.
