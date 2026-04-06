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

```bash
curl -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d @my-process.bpmn
```

## Start an instance

```bash
curl -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"my-process"}'
```
