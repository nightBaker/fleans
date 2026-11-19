---
title: Logs and Debugging
description: Structured-logging field reference, EventId ranges, and how to query Fleans-emitted logs.
---

This page documents the structured-logging contract Fleans emits via `[LoggerMessage]` source generators. For health checks, metrics, and tracing, see [Observability](/fleans/reference/observability/).

## Logging contract

Fleans uses **`[LoggerMessage]` source-generated logging exclusively** — no `ILogger.LogInformation(...)` extension-method calls. Every state mutation of a workflow instance emits a structured log entry with a stable EventId. This means:

<figure class="arch-diagram" style="--arch-ratio: 1080 / 558">
  <iframe data-arch-src="/fleans/diagrams/logs-debugging-flow.html" src="/fleans/diagrams/logs-debugging-flow.html?embed=1" title="Logs debugging flow" loading="lazy"></iframe>
  <figcaption>Each state mutation emits a source-generated log entry with a stable EventId, enriched with grain-call scope fields; filter by level, then query your log backend by EventId range or <code>WorkflowInstanceId</code>. <a href="/fleans/diagrams/logs-debugging-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

- **EventIds are stable** — filter by EventId, not by message text. Every EventId names one event.
- **Field names are typed and consistent** — log queries on `WorkflowInstanceId` always hit the same field.
- **No format-string drift** — the source generator validates the format string against the parameters at compile time.

If you write a custom-task worker, log the same way from your handler so its lines can be filtered alongside the engine's.

## EventId ranges

Ranges are allocated per component. Numbers inside a range that are not listed are Debug or Information lines of the same component.

| Range | Component |
|-------|-----------|
| 1000–1069 | `WorkflowInstance` — activity execution, transitions, gateways, user tasks. 1004 failing an activity (Warning); 1066 user-task claim rejected (Warning) |
| 1070–1079 | `WorkflowInstance` — pending events and event sub-processes (1078 root-scope listeners armed) |
| 1080–1089 | `WorkflowInstance` — complex gateway |
| 1090–1099 | `WorkflowInstance` — escalation |
| 1100–1109 | `WorkflowInstance` — transaction sub-process |
| 1110–1119 | `WorkflowInstance` — compensation |
| 1120 | `WorkflowInstance` — workflow failed: an unhandled activity failure left no active activities (Error) |
| 1131–1149 | `WorkflowInstance` — event store and instance state: 1131 snapshot write failed, retrying (Error); 1132 publishing the `InstanceAdvanced` notification to the read-model projector failed, retrying (Warning); 1133 read model differs from the event log on activation, asking the projector again (Information); 1134 notification published after a failed attempt; 1135 publish retries gave up, retried on the next activation (Error); 1136 reading the read-model row failed (Warning); 1137 the journal moved during a failed or conflicting append, activation retired (Error); 1138 append failed, journal unchanged, live state kept (Warning); 1139 append failed but the journal moved, retried with the same batch id (Warning); 1140 variables over `Fleans:Variables:MaxSizeBytes` rejected (Warning); 1141 state served without its process definition (Warning); 1143 snapshot written after a failed attempt; 1144 snapshot retries gave up, the next activation writes it (Error); 1145 a snapshot retry failed (Warning); 1147 repeated start ignored; 1149 completion of an already-pruned entry ignored (Warning) |
| 1150–1153 | `WorkflowInstance` — multiple (catch / throw / boundary) events |
| 1160–1169 | `WorkflowInstance` — conditions, script dispatch and custom-task hand-off: 1161 a condition failed to evaluate, its activity failed (Error); 1162 a multi-instance completion condition failed, the iterations carry on (Error); 1165 a script task got no result within `Fleans:Worker:ScriptResultTimeout`, dispatched again (Warning); 1166 no result after the last dispatch, the script task failed (Error); 1167 an activating instance dispatched its executing script tasks again; 1168 that dispatch failed (Error); 1169 a custom task could not be handed to the external-task queue, the activity failed (Error) |
| 1170–1174 | `WorkflowInstance` — [instance migration](/fleans/guides/instance-migration/): 1170 started, 1171 migrated, 1172 rejected, 1173 repeated (no-op), 1174 user task redefined |
| 1200–1250 | Effect handlers — child-workflow start (1200–1209; 1201 child start failed, Error), timer reminders (1210–1219), message subscriptions (1220–1229), signal subscriptions and throws (1230–1239), user-task persistence (1240–1249); 1250 several handlers match one effect (Warning). Registration failures in this range are Warnings and fail the activity |
| 3000–3099 | `WorkflowInstance` — state changes: 3030 workflow cancelled (Warning); 3031–3032 escalation warnings; 3034 a timer fired after its host activity was resolved, ignored (Warning) |
| 4070–4076 | Read-model projector: 4070 projected, 4071 notification skipped — already projected (both Debug); 4072 projection failed, the stream redelivers (Warning); 4073 stream error (Error); 4074 notification for a purged instance skipped; 4075 batch written (Debug); 4076 batch failed, every projection in it redelivered (Warning). The rest of 4000–4099 is retired |
| 4100–4104 | `UserTaskGrain` — registered, claim updated, completed, discarded, redefined by a migration |
| 5000–5002 | `WorkflowEventsPublisher` — 5001 unhandled event type dropped (Warning) |
| 6000–6006, 6050 | `ProcessDefinitionGrain` — deploy, enable, disable; 6006 hosts not told that start events changed (Warning); 6050 definition loaded into a silo's cache (Debug) |
| 7000–7022 | `WorkflowCommandService` — 7020 deployment request submitted; 7021 completing / 7022 failing an activity instance through the API |
| 8000–8003, 8011–8012 | `TimerStartEventSchedulerGrain` — timer start events (8011 cycle re-armed, 8012 fire skipped because the process is disabled) |
| 8004–8010 | `/UserTasks/*` — 8009 body `UserId` differs from the token, 8010 token without a user-id claim (Warning) |
| 8100 | Unhandled exception in the API request pipeline (Error) |
| 8200–8201 | `/Deployments/*` — body `UserId` differs from the token (8200), token without a user-id claim (8201) (Warning) |
| 8300–8302 | [Admission control](/fleans/reference/configuration/#admission-control) (`fleans-api`): 8300 limit lowered (Warning), 8301 limit raised, 8302 request rejected with 429 (both Debug) |
| 9000–9009 | `MessageCorrelationGrain` — message subscriptions registered / removed and deliveries |
| 9010 | BPMN parsing — activation condition ignored on a fork (Warning) |
| 9020–9024 | `ScriptExecutorGrain`: 9021 script failed (Error); 9022 the instance rejected the script's result, e.g. variables over the size limit — activity failed (Warning); 9023 reporting the result failed transiently, retrying (Warning); 9024 reporting gave up — the instance dispatches the script again after its result timeout (Critical) |
| 9100–9125 | `MessageStartEventListenerGrain` (9100–9109), `SignalCorrelationGrain` (9110–9122; 9121 a broadcast's deliveries handed to the stream), `SignalDeliveryGrain` (9123–9125: 9124 a signal delivery failed (Warning) — the instance keeps its subscription for the next broadcast) |
| 9200–9207 | `SignalStartEventListenerGrain` — signal start registrations |
| 9300–9329 | Conditional start registrations (9300–9319); `CustomTaskCatalogGrain` (9321–9329: 9326/9327 gRPC worker registered / unregistered, 9328 task type became private or public, 9329 allow-list set) |
| 9340–9369 | Reserved for the [fleans-plugins](https://github.com/nightBaker/fleans-plugins) workers (REST Caller 9340–9349, Camunda external tasks 9350–9369) |
| 9370–9392 | Message, signal and conditional starts — 9370 an instance started, 9371 no process subscribed to the message or signal (Warning), 9372 a start failed (Error), 9373 disabled process skipped (Warning), 9376 conditional start fired, 9380 conditional evaluation failed (Error); 9386 a host could not subscribe to deployment changes, its start-event cache refreshes only every 30 s (Warning); 9392 a host did not take a deployment change (Warning) |
| 10000–10006 | `TimerCallbackGrain` — timer reminders armed, fired, cancelled; 10006 (Debug) a due timer deferred a minute because every timer delivery slot of the silo was busy (`Fleans:Timers:DeliveryConcurrency`) |
| 11100–11118 | Kafka stream provider — 11101 `acks` not `all` (Warning); 11104 metadata error (Error); 11107 / 11113 a record could not be deserialized (Error); 11108 consumer error, 11109 consume failed (Warning); 11114 batch publish failed (Error); 11118 a topic has more than one partition, records may be misordered (Warning) |
| 11200–11201 | AWS MSK IAM — 11200 token refresh failed (Error; check the IAM policy for `kafka-cluster:Connect` / `WriteData` / `ReadData`); 11201 `OAuthBearerSetTokenFailure` threw after a refresh failure (Warning; typically a shutdown race) |
| 11300–11301 | Grain placement — 11300 no Core / Combined silo, 11301 no Worker / Combined silo: the grain is placed on any silo instead (Warning). See [Common issues](/fleans/reference/troubleshooting-common-issues/#warnings-11300--11301-no-core-or-worker-silo) |
| 11310–11311 | Placement-role assertion. Allocated but **not emitted** by current releases: the startup check that would log them is not active ([#787](https://github.com/nightBaker/fleans/issues/787)) |
| 11400 | Cluster ring — a silo that joined a brand-new cluster was told by Orleans that it is dead itself, and Fleans ignored that (Warning). Expected once per silo on the first start of a fresh cluster; not actionable |
| 12000–12007 | Kafka dead-letter topic — 12000 record dead-lettered (Warning), 12001 dead-letter publish failed or unacknowledged (Error), 12005 re-consume failed (Error) |
| 13000–13099 | `fleans-web` admin UI — page load, deploy, editor, AI assistant, promotion and migration-dialog failures (Warning / Error). Prompts and diagram contents are never logged |
| 14100–14108 | Retention — 14100–14102 instance purge started / batch purged / finished, 14103 expired message-deduplication records purged; 14104–14108 monthly read-model partitions on PostgreSQL (14107 partitions not created, 14108 partitions busy — skipped, both Warning) |
| 14200–14202 | Editor presence (Debug) |
| 14300–14305 | Deployment requests — 14300 submitted, 14301 approved, 14302 rejected, 14303 withdrawn, 14304 deployed; 14305 approved but the deploy failed, retry possible (Warning) |
| 14400–14405 | Messages sent with an `IdempotencyKey` — 14400 a repeat answered with the first outcome; 14404 an interrupted send resumed with its recorded instance ids (Warning) |
| 14500–14599 | [External workers](/fleans/guides/external-workers/) over gRPC — 14500 dispatching; 14502 no worker took the task, 14504 worker disconnected, 14505 execution timeout (Warning); 14510 task failed (Error); 14511 reporting the failure to the instance failed (Critical); 14530/14531 worker joined / left a pool, 14532 lease expired, 14533 worker refused (Warning); 14540/14541 worker connected / disconnected; 14570–14599 are logged by the worker SDK in your worker process |
| 14600–14602 | Instance migration batches — 14600 started, 14601 finished with counts, 14602 an instance failed (Warning) |

## Log-scope fields

Every grain call that belongs to a workflow instance runs inside a logging scope, propagated across grain boundaries via Orleans `RequestContext`. The scope carries these structured fields:

| Field | Value |
|-------|-------|
| `WorkflowId` | The process id from the BPMN (`<process id="…">`) |
| `ProcessDefinitionId` | The deployed version, e.g. `order:3:…` |
| `WorkflowInstanceId` | The instance's Guid |
| `BusinessId` | The instance's business id, `-` when none was set; not inherited by call-activity children |
| `ActivityId` | The BPMN element id, when the line belongs to one activity; `-` otherwise |
| `ActivityInstanceId` | Reserved; `-` in current releases |
| `VariablesId` | Reserved; `-` in current releases |

Lines logged outside a call from the instance — the "creating instance" line on start, and timer reminder callbacks until they call into the instance — lack these fields. Look up the instance's `WorkflowInstanceId` once (it is on every matched line) and filter on that for the full picture.

The OTLP log exporter always includes scopes, so in a structured-log backend (Seq, Loki, Datadog) you query these field names directly — they arrive as attributes, not embedded in the message text. Console logs include them only when the console formatter's `IncludeScopes` is on.

## Filtering by log level

Per-category log levels use the standard `Microsoft.Extensions.Logging` configuration. With the released containers, set them as environment variables (`Logging__LogLevel__<category>`, with `.` kept in the category name). For example, to keep workflow-instance lines at Warning and above:

```bash
Logging__LogLevel__Default=Information
Logging__LogLevel__Fleans.Application.Grains.WorkflowInstance=Warning
Logging__LogLevel__Orleans=Warning
```

This uses the standard `LogLevel` configuration — no additional packages or custom filter providers are needed.

## Related

- [Observability](/fleans/reference/observability/) — health checks, metrics, OTel collector wiring.
- [Orleans Dashboard](/fleans/reference/observability/#orleans-dashboard) — real-time cluster view.
- [Common issues](/fleans/reference/troubleshooting-common-issues/) — symptoms, checks and actions.
