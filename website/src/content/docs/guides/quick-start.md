---
title: Quick Start
description: Run Fleans locally in under 5 minutes.
---

## Prerequisites

- .NET 10 SDK
- Docker (for Redis via Aspire)

## Clone and run

```bash
git clone https://github.com/nightBaker/fleans.git
cd fleans/src/Fleans
dotnet run --project Fleans.Aspire
```

Aspire launches the API, Blazor admin UI, and Redis. Open the Aspire dashboard URL from the console
to find the Web app.

:::note[Expected output]
Aspire prints (among other startup logs) a dashboard URL like:

```
Login to the dashboard at https://localhost:<port>/login?t=<token>
```

Both `<port>` (default `15888` unless overridden by `ASPNETCORE_URLS` or your
`launchSettings.json`) and `<token>` (a fresh per-boot value) will differ
run-to-run — use whatever the console prints. Open that URL to reach the
Aspire dashboard, from which you can click through to the `Fleans.Web`
service (the Admin UI).
:::

## Deploy a workflow

Fleans deploys workflows through the **Admin UI** (Blazor editor), not via a REST endpoint.

1. Open the **Web app** — find its URL on the Aspire dashboard
2. Navigate to the **Editor** page
3. Import or paste your BPMN XML, then click **Deploy**

A sample BPMN file is available to get you started:
[**my-process.bpmn**](/samples/my-process.bpmn) — a minimal workflow with a single script task
that sets a `greeting` variable.

```xml
<!-- Excerpt from my-process.bpmn -->
<scriptTask id="greet" name="Set Greeting" scriptFormat="csharp">
  <script>_context.greeting = "Hello from Fleans!";</script>
</scriptTask>
```

Download the file, open the Editor, import it, and click **Deploy**.

After clicking **Deploy**, the Editor shows a green success message bar reading
`Deployed 'my-process' v1 (N activities, M flows)`, the breadcrumb displays the
process key, and an accent-colored `v1` badge appears next to it. Deploying
again would produce `v2`, and so on.

:::note
Script tasks execute automatically — no external `complete-activity` call is needed.
The engine runs the C# script inline and advances the workflow to the next element.
:::

## Start an instance

Once the workflow is deployed, start an instance via the API:

```bash
curl -X POST https://localhost:7140/Workflow/start \
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
immediately. You can verify the result in the Admin UI. From the **Workflows**
page, click the `my-process` row — this opens the process-instances list for
that definition (route: `/process-instances/my-process/1`). The instance you
just started appears there as `Completed`, and opening it shows the variables
panel containing `greeting = "Hello from Fleans!"`.
