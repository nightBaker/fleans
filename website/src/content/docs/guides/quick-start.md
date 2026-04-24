---
title: Quick Start
description: Run Fleans locally in under 5 minutes.
---

## Prerequisites

- .NET 10 SDK
- Docker (for Redis via Aspire)

## Clone and run

```bash
git clone https://github.com/<github-user>/fleans.git
cd fleans/src/Fleans
dotnet run --project Fleans.Aspire
```

Aspire launches the API, Blazor admin UI, and Redis. Open the Aspire dashboard URL from the console
to find the Web app.

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

Because `my-process` contains only a script task, the instance runs to completion
immediately. You can verify the result in the Admin UI — the instance will show
a `greeting` variable with the value `"Hello from Fleans!"`.
