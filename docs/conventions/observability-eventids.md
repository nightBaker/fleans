# Observability EventId ranges

Contributor-facing reference for the structured-logging EventId allocation. Allocate new
`[LoggerMessage]` EventIds from the appropriate range below. New ranges must be added
here when a new class starts emitting structured logs.

For the architectural background (filter pattern, scope-field semantics, RequestContext
propagation), see [`docs/plans/2026-02-08-structured-workflow-logging.md`](../plans/2026-02-08-structured-workflow-logging.md).

## Allocated ranges

| Range | Class | Notes |
|-------|-------|-------|
| 1000–1199 | `WorkflowInstance` | Sub-ranges: 1066 UserTaskClaim rejection (Warning); 1070–1079 pending events / event sub-processes; 1078 root-scope listeners; 1080–1089 complex gateway; 1090–1099 escalation; 1100–1109 transaction sub-process; 1110–1119 compensation; 1120 workflow failed by unhandled activity failure (Error, #762); 1131–1139 event store (1131 snapshot write failed (Error), 1132 projection write failed — retrying, 1133 projection behind on activation, 1134 projection caught up after retry, 1135 projection retries gave up (Error), 1136 projection version read failed); 1140 variables over `Fleans:Variables:MaxSizeBytes` rejected (Warning); 1141 state snapshot served without its process definition (Warning) |
| 2000–2099 | `ActivityInstance` | |
| 3000–3099 | `WorkflowInstanceState` | Includes 3030–3032 escalation warnings |
| 4000–4099 | Event handlers | `Fleans.Application/Events/Handlers/*`; `CustomTaskHandlerBase` (`Fleans.Worker`) uses 4030–4035, 4037–4039, 4050: 4030 handling INFO, 4031 execute failed ERROR, 4032 stream completed INFO, 4033 stream error ERROR, 4034 FailActivity failed CRITICAL, 4035 activated INFO, 4037 task-type mismatch WARNING, 4038 skipped finished activity INFO, 4039 transient grain-call failure rethrown WARNING, 4050 cancelled on deactivation INFO. **Known collision:** 4030–4034 are also used by `WorkflowEvaluateCompletionConditionEventHandler` — filter by logger category; pick new custom-task ids from 4040+ only after checking both classes. |
| 5000–5099 | `WorkflowEventsPublisher` | |
| 6000–6099 | `WorkflowInstanceFactoryGrain` | |
| 7000–7099 | `WorkflowEngine` | |
| 8000–8099 | `TimerStartEventSchedulerGrain` (8000–8003), `UserTasksController` (8004–8010) | 8009 body-UserId/token mismatch rejection (Warning); 8010 token without user-id claim (Warning) |
| 9000–9099 | `BpmnConverter` | |
| 9340–9349 | `RestCallerHandler` (`Fleans.Plugins.RestCaller`) | 9340 sending, 9341 received, 9342 timeout, 9343 network error, 9344 non-success status, 9345 idempotency key, 9346 JSON parse failed, 9347 response body over limit (Warning) |
| 9350–9369 | *Reserved:* external [fleans-camunda-external-task](https://github.com/nightBaker/fleans-camunda-external-task) plugin | Allocated in that repository; don't reuse here so its logs stay unambiguous in a shared cluster. |
| 10000–10099 | `TimerCallbackGrain` | |
| 11000–11099 | `KafkaProductionPresetExtensions` | 11000 preset-applied INFO |
| 11100–11199 | `KafkaQueueAdapter`, `KafkaQueueAdapterReceiver`, `KafkaQueueAdapterFactory` | 11100 SSL-no-paths OS-trust-store WARNING (shared EventId across all three sibling classes); 11101 acks-not-all WARNING; 11102 single-broker RF fallback INFO; 11103 RF>brokers fallback WARNING; 11104 GetMetadata error ERROR; 11105 topics created INFO; 11106 topics already existed INFO; 11107 record deserialization failed, skipped (DLQ off) ERROR; 11108 consumer error WARNING; 11109 consume failed WARNING; 11110 offset commit failed WARNING; 11111 consumer close failed WARNING; 11112 DLQ enabled but adapter not created yet, handler falls back to no-op WARNING; 11113 record deserialization failed, routing to DLQ ERROR |
| 11200–11299 | `MskIamTokenRefresher` | 11200 token-refresh-failed ERROR, 11201 set-token-failure-threw WARNING |
| 12000–12099 | Kafka DLQ: `KafkaDlqFailureHandler`, `KafkaDeadLetterPublisher` | 12000 record dead-lettered (with failure reason) WARN; 12001 DLQ publish failed or not acknowledged within 30 s (may still be delivered) ERROR; 12002 subscription-init failure WARN; 12003 non-Kafka sequence token WARN; 12004 offset no longer retained WARN; 12005 re-consume failed ERROR; 12006 re-consume timed out WARN; 12007 re-consume client error (auth/SSL/connectivity) WARN |
| 99000–99099 | `ProbeHandler` (test-only, `Fleans.E2E.PluginHost`) | 99000 started, 99001 completed, 99002 cancelled, 99003 callback failed. Never shipped. |

## Adding a new range

1. Pick an unused range (e.g., 11000–11099) in increments of 100.
2. Add a row to the table above with the class name.
3. Add the row to the website-published Logs & Debugging reference if user-facing — see
   `website/src/content/docs/reference/troubleshooting-logs-debugging.md`.

## Log-scope fields

Every grain call gets a scope wrapping (via `WorkflowLoggingScopeFilter`). The scope fields
are propagated through `RequestContext`:

- `WorkflowId`
- `ProcessDefinitionId`
- `WorkflowInstanceId`
- `ActivityId`
- `ActivityInstanceId`
- `VariablesId`

These are the queryable fields in structured-log backends. Filter by them, not by EventId
numeric ranges, in user-facing log queries.
