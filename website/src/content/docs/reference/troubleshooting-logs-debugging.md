---
title: Logs and Debugging
description: Structured-logging field reference, EventId ranges, and how to query Fleans-emitted logs.
---

This page documents the structured-logging contract Fleans emits via `[LoggerMessage]` source generators. For health checks, metrics, and tracing, see [Observability](/fleans/reference/observability/).

## Logging contract

Fleans uses **`[LoggerMessage]` source-generated logging exclusively** — no `ILogger.LogInformation(...)` extension-method calls. Every state mutation in `WorkflowInstance` and `ActivityInstance` emits a structured log entry via a `private partial void` declared on a partial class. This means:

- **EventIds are stable** — filter by EventId, not by message text.
- **Field names are typed and consistent** — log queries on `WorkflowInstanceId` always hit the same field.
- **No format-string drift** — the source generator validates the format string against the parameters at compile time.

If you author a custom-task plugin, follow the same pattern (see the [adding a BPMN activity](https://github.com/nightBaker/fleans/blob/main/docs/conventions/adding-a-bpmn-activity.md) convention for the boilerplate).

## EventId ranges

| Range | Component |
|-------|-----------|
| 1000–1199 | `WorkflowInstance` |
| 1070–1079 | `WorkflowInstance` — pending events and event sub-processes |
| 1080–1089 | `WorkflowInstance` — complex gateway |
| 1090–1099 | `WorkflowInstance` — escalation |
| 1100–1109 | `WorkflowInstance` — transaction sub-process |
| 1110–1119 | `WorkflowInstance` — compensation |
| 1066 | `UserTaskClaim` rejection (Warning) |
| 2000–2099 | `ActivityInstance` |
| 3000–3099 | `WorkflowInstanceState` |
| 3030–3032 | `WorkflowInstance` — escalation warnings |
| 4000–4099 | Event handlers |
| 5000–5099 | `WorkflowEventsPublisher` |
| 9000–9099 | `BpmnConverter` |
| 10000–10099 | `TimerCallbackGrain` |

For the full per-EventId deep dive and reserved bands, see [`docs/plans/2026-02-08-structured-workflow-logging.md`](https://github.com/nightBaker/fleans/blob/main/docs/plans/2026-02-08-structured-workflow-logging.md).

## Log-scope fields

Every grain call is wrapped in a `BeginScope` by `WorkflowLoggingScopeFilter` (an Orleans `IIncomingGrainCallFilter`). The scope carries these structured fields, propagated automatically across grain boundaries via `RequestContext`:

| Field | Type | Set by |
|-------|------|--------|
| `WorkflowId` | string | `WorkflowInstance` |
| `ProcessDefinitionId` | string | `WorkflowInstance` |
| `WorkflowInstanceId` | string | `WorkflowInstance` |
| `ActivityId` | string | `WorkflowInstance` / `ActivityInstance` |
| `ActivityInstanceId` | string | `WorkflowInstance` / `ActivityInstance` |
| `VariablesId` | string | `ActivityInstance` |

In a structured-log backend (Seq, Loki, Datadog), query against these field names directly — they are emitted as JSON properties, not embedded in the message text.

## Filtering by log level

In `appsettings.json`, you can set per-category log levels using the standard `Microsoft.Extensions.Logging` configuration. For example, to suppress verbose workflow logging while keeping escalation warnings visible:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Fleans.Application.Grains.WorkflowInstance": "Warning",
      "Microsoft.Orleans": "Warning"
    }
  }
}
```

This uses the standard `LogLevel` configuration — no additional packages or custom filter providers are needed.

## Related

- [Observability](/fleans/reference/observability/) — health checks, metrics, OTel collector wiring.
- [Orleans Dashboard](/fleans/reference/observability/#orleans-dashboard) — real-time cluster view.
- [`docs/plans/2026-02-08-structured-workflow-logging.md`](https://github.com/nightBaker/fleans/blob/main/docs/plans/2026-02-08-structured-workflow-logging.md) — contributor-side logging architecture.
