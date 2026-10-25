---
title: Logs and Debugging
description: Structured-logging field reference, EventId ranges, and how to query Fleans-emitted logs.
---

This page documents the structured-logging contract Fleans emits via `[LoggerMessage]` source generators. For health checks, metrics, and tracing, see [Observability](/fleans/reference/observability/).

## Logging contract

Fleans uses **`[LoggerMessage]` source-generated logging exclusively** — no `ILogger.LogInformation(...)` extension-method calls. Every state mutation in `WorkflowInstance` and `ActivityInstance` emits a structured log entry via a `private partial void` declared on a partial class. This means:

<figure class="arch-diagram" style="--arch-ratio: 1080 / 558">
  <iframe data-arch-src="/fleans/diagrams/logs-debugging-flow.html" src="/fleans/diagrams/logs-debugging-flow.html?embed=1" title="Logs debugging flow" loading="lazy"></iframe>
  <figcaption>Each state mutation emits a source-generated log entry with a stable EventId, enriched with grain-call scope fields; filter by level, then query your log backend by EventId range or <code>WorkflowInstanceId</code>. <a href="/fleans/diagrams/logs-debugging-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

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
| 1120 | `WorkflowInstance` — workflow failed: an unhandled activity failure left no active activities (Error) |
| 1131–1136 | `WorkflowInstance` — event store: 1131 snapshot write failed (Error; if it keeps failing the instance eventually cannot activate); 1132 publishing the `InstanceAdvanced` notification to the read-model projector failed, retrying (Warning); 1133 read model differs from the event log on activation (a notification was lost or is still on its way), asking the projector again (Information); 1134 notification published after a failed attempt (Information); 1135 publish retries gave up, retried on the next activation (Error); 1136 reading the read-model row failed, asking the projector again (Warning) |
| 1140 | `WorkflowInstance` — variables over `Fleans:Variables:MaxSizeBytes` rejected (Warning) |
| 1150–1153 | `WorkflowInstance` — multiple (catch / throw / boundary) events |
| 1160–1168 | `WorkflowInstance` — conditions and script tasks: 1161 a condition failed to evaluate, its activity failed (Error); 1162 a multi-instance completion condition failed, the iterations carry on (Error); 1165 a script task got no result within `Fleans:Worker:ScriptResultTimeout`, dispatched again (Warning); 1166 no result after the last dispatch, the script task failed (Error); 1167 an activating instance dispatched its executing script tasks again (Information); 1168 that dispatch failed (Error). 1160, 1163 and 1164 are Debug |
| 1200–1299 | Effect handlers — child-workflow start (1200–1209), timer reminders (1210–1219), message subscriptions (1220–1229), signal subscriptions and throws (1230–1239), user-task persistence (1240–1249) |
| 1066 | `UserTaskClaim` rejection (Warning) |
| 2000–2099 | `ActivityInstance` |
| 3000–3099 | `WorkflowInstanceState` |
| 3030–3032 | `WorkflowInstance` — escalation warnings |
| 4000–4099 | Event handlers, including custom-task handlers (4030–4039, 4050) and the read-model projector (4070 projected, 4071 notification skipped — already projected (both Debug); 4072 projection failed, the stream redelivers (Warning); 4073 stream error (Error); 4074 notification for a purged instance skipped (Information)) |
| 5000–5099 | `WorkflowEventsPublisher` |
| 9000–9099 | `MessageCorrelationGrain` (9000–9009), `BpmnConverter` (9010–9019), `ScriptExecutorGrain` (9020–9029: 9021 script failed (Error); 9022 the instance rejected the script's result, e.g. variables over the size limit — activity failed (Warning); 9023 reporting the result failed transiently, retrying (Warning); 9024 reporting gave up — the instance dispatches the script again after its result timeout (Critical)) |
| 9100–9199 | `MessageStartEventListenerGrain` (9100–9109), `SignalCorrelationGrain` (9110–9119) |
| 10000–10099 | `TimerCallbackGrain` |
| 11200 | `Fleans.Streaming.Kafka.AwsMsk` — MSK IAM token refresh failed (Error); check IAM policy for `kafka-cluster:Connect` / `WriteData` / `ReadData` |
| 11201 | `Fleans.Streaming.Kafka.AwsMsk` — `OAuthBearerSetTokenFailure` threw after token-refresh failure (Warning); typically a shutdown race, not actionable |
| 11300–11399 | Grain placement — 11300 no core silo, 11301 no worker silo, 11302 no projector or core silo (Warning); 11311 placement-role violation (Error) |
| 11400 | Cluster ring — a silo that joined a brand-new cluster was told by Orleans that it is dead itself, and Fleans ignored that so the silo keeps routing reminders to itself (Warning). Expected once per silo on the first start of a fresh cluster; not actionable |
| 7020 | Deployment request submitted (Information) |
| 8200–8201 | `/Deployments/*` — body `UserId` differs from the token (8200), token without a user-id claim (8201) (Warning) |
| 13000–13099 | `Fleans.Web` admin UI — 13005 promotion failed (Error), 13010 promoting a version to another environment (Information), 13011 a call to another environment (promotion or the environments board) failed with its HTTP status (Warning) |
| 14300–14305 | Deployment requests — 14300 submitted, 14301 approved, 14302 rejected, 14303 withdrawn, 14304 deployed (Information); 14305 approved but the deploy failed, retry possible (Warning) |
| 14100–14102 | Retention purge (`POST /Instances/purge`) — 14100 purge started, 14101 batch purged, 14102 purge finished (all Information) |

For the full per-EventId deep dive and reserved bands, see [`docs/plans/2026-02-08-structured-workflow-logging.md`](https://github.com/nightBaker/fleans/blob/main/docs/plans/2026-02-08-structured-workflow-logging.md).

## Log-scope fields

Every grain call is wrapped in a `BeginScope` by `WorkflowLoggingScopeFilter` (an Orleans `IIncomingGrainCallFilter`). The scope carries these structured fields, propagated automatically across grain boundaries via `RequestContext`:

| Field | Type | Set by |
|-------|------|--------|
| `WorkflowId` | string | `WorkflowInstance` |
| `ProcessDefinitionId` | string | `WorkflowInstance` |
| `WorkflowInstanceId` | string | `WorkflowInstance` |
| `BusinessId` | string | `WorkflowInstance`, stream handlers (script, condition, custom task) — the instance's business id, `-` when none was set; not inherited by call-activity children. Lines logged outside a call from the instance (e.g. timer reminders) lack it — filter on `WorkflowInstanceId` for the full picture |
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
