# Observability EventId ranges

Contributor-facing reference for the structured-logging EventId allocation. Allocate new
`[LoggerMessage]` EventIds from the appropriate range below. New ranges must be added
here when a new class starts emitting structured logs.

**Every EventId names one event.** `LoggerEventIdUniquenessTests` (`Fleans.Application.Tests`)
scans `src/Fleans/**/*.cs` and fails when two `[LoggerMessage]` declarations share an EventId
— alerts keyed on an EventId must not fire on an unrelated event. The only exceptions are
listed in the test's `SharedEventIds` (today 11100: one event logged from three sibling Kafka
classes); add to it only for the same event, and note it here.

For the architectural background (filter pattern, scope-field semantics, RequestContext
propagation), see [`docs/plans/2026-02-08-structured-workflow-logging.md`](../plans/2026-02-08-structured-workflow-logging.md).

## Allocated ranges

| Range | Class | Notes |
|-------|-------|-------|
| 1000–1199 | `WorkflowInstance` | Sub-ranges: 1066 UserTaskClaim rejection (Warning); 1070–1079 pending events / event sub-processes; 1078 root-scope listeners; 1080–1089 complex gateway; 1090–1099 escalation; 1100–1109 transaction sub-process; 1110–1119 compensation; 1150–1153 multiple events (1150 multiple catch registered, 1151 multiple catch fired, 1152 multiple throw fired, 1153 multiple boundary fired); 1120 workflow failed by unhandled activity failure (Error, #762); 1131–1139 event store (1131 snapshot write failed (Error), 1132 `InstanceAdvanced` publish failed — retrying, 1133 read-model row differs from the journal on activation — projector asked again, 1134 publish succeeded after retry, 1135 publish retries gave up (Error), 1136 read-model row read failed, 1137 journal moved during a failed or conflicting append — activation retired (Error), 1138 append failed, journal unchanged — live state kept (Warning), 1139 append failed but the journal moved — retried with the same batch id, which the store confirms (Warning, #877)); 1140 variables over `Fleans:Variables:MaxSizeBytes` rejected (Warning); 1141 state snapshot served without its process definition (Warning); 1142 pending event journaled and queued (Debug); 1147 repeated start of an already-started instance ignored (#863); 1148 terminal instance deactivating once idle (Debug, #860) |
| 1200–1299 | Effect handlers (`Fleans.Application/Effects/*`) | 1200–1209 `WorkflowLifecycleEffectHandler` (1200 starting child workflow, 1201 child start failed ERROR); 1210–1219 `TimerEffectHandler` (1210 reminder registered, 1211 reminder registration failed WARNING, 1212 reminder unregistered, 1213 unregister failed WARNING); 1220–1229 `MessageEffectHandler` (1220 subscribed, 1221 subscribe failed WARNING, 1222 unsubscribe failed WARNING); 1230–1239 `SignalEffectHandler` (1230 subscribed, 1231 subscribe failed WARNING, 1232 signal thrown, 1233 unsubscribe failed WARNING, 1234 throw failed WARNING); 1240–1249 `UserTaskEffectHandler` (1240 registration failed, 1241 completion persistence failed, 1242 claim update failed — all WARNING) |
| 2000–2099 | `ActivityInstance` | |
| 3000–3099 | `WorkflowInstanceState` | Includes 3030–3032 escalation warnings |
| 4000–4099 | Event handlers | `Fleans.Application/Events/Handlers/*`; `CustomTaskHandlerBase` (`Fleans.Worker`) uses 4030–4035, 4037–4039, 4050: 4030 handling INFO, 4031 execute failed ERROR, 4032 stream completed INFO, 4033 stream error ERROR, 4034 FailActivity failed CRITICAL, 4035 activated INFO, 4037 task-type mismatch WARNING, 4038 skipped finished activity INFO, 4039 transient grain-call failure rethrown WARNING, 4050 cancelled on deactivation INFO. Per handler: 4000–4009 `WorkflowEvaluateConditionEventHandler`, 4010–4019 `WorkflowExecuteScriptEventHandler`, 4020–4029 `WorkflowEvaluateActivationConditionEventHandler`, 4060–4066 `WorkflowEvaluateCompletionConditionEventHandler`; 4070–4079 `WorkflowInstanceProjector` (`Fleans.Application/Projections/`, #861): 4070 projected (Debug), 4071 notification skipped — already projected (Debug), 4072 projection failed, redelivered (Warning), 4073 stream error (Error), 4074 notification for a purged instance (no journal) skipped (Information). |
| 4100–4199 | `UserTaskGrain` | |
| 5000–5099 | `WorkflowEventsPublisher` | |
| 6000–6099 | `ProcessDefinitionGrain` (6000–6049), `WorkflowDefinitionCache` (6050–6059) | 6050 definition loaded into the silo cache (Debug) |
| 7000–7099 | `WorkflowCommandService` | 7020 deployment request submitted (#900) |
| 8000–8099 | `TimerStartEventSchedulerGrain` (8000–8003, 8011 cycle re-armed, 8012 fire skipped — process disabled), `UserTasksController` (8004–8010) | 8009 body-UserId/token mismatch rejection (Warning); 8010 token without user-id claim (Warning) |
| 8100–8199 | `GlobalExceptionHandler` (`Fleans.Api`) | |
| 8200–8299 | `DeploymentsController` (`Fleans.Api`) | 8200 body-UserId/token mismatch rejection, 8201 token without user-id claim (both Warning) |
| 8300–8399 | `AdaptiveConcurrencyLimiter` (`Fleans.Api/Admission`, #922) | 8300 admission limit lowered (Warning), 8301 limit raised (Debug), 8302 request rejected with 429 (Debug) |
| 9000–9099 | `MessageCorrelationGrain` (9000–9009), `BpmnConverter` (9010–9019), `ScriptExecutorGrain` (`Fleans.Worker`, 9020–9029) | 9010 activation condition ignored on a fork (Warning) |
| 9100–9199 | `MessageStartEventListenerGrain` (9100–9109), `SignalCorrelationGrain` (9110–9119) | |
| 9200–9299 | `SignalStartEventListenerGrain` | |
| 9300–9339 | `ConditionalStartEventListenerGrain` (9300–9309), `ConditionalStartEventRegistryGrain` (9310–9319), `CustomTaskCatalogGrain` (9320–9329), `CustomTaskPluginRegistrar` (`Fleans.Worker`, 9330–9339) | |
| 9340–9349 | `RestCallerHandler` (`Fleans.Plugins.RestCaller`) | 9340 sending, 9341 received, 9342 timeout, 9343 network error, 9344 non-success status, 9345 idempotency key, 9346 JSON parse failed, 9347 response body over limit (Warning) |
| 9350–9369 | *Reserved:* external [fleans-camunda-external-task](https://github.com/nightBaker/fleans-camunda-external-task) plugin | Allocated in that repository; don't reuse here so its logs stay unambiguous in a shared cluster. |
| 10000–10099 | `TimerCallbackGrain` | |
| 11000–11099 | `KafkaProductionPresetExtensions` | 11000 preset-applied INFO |
| 11100–11199 | `KafkaQueueAdapter`, `KafkaQueueAdapterReceiver`, `KafkaQueueAdapterFactory` | 11100 SSL-no-paths OS-trust-store WARNING (shared EventId across all three sibling classes); 11101 acks-not-all WARNING; 11102 single-broker RF fallback INFO; 11103 RF>brokers fallback WARNING; 11104 GetMetadata error ERROR; 11105 topics created INFO; 11106 topics already existed INFO; 11107 record deserialization failed, skipped (DLQ off) ERROR; 11108 consumer error WARNING; 11109 consume failed WARNING; 11110 offset commit failed WARNING; 11111 consumer close failed WARNING; 11112 DLQ enabled but adapter not created yet, handler falls back to no-op WARNING; 11113 record deserialization failed, routing to DLQ ERROR |
| 11200–11299 | `MskIamTokenRefresher` | 11200 token-refresh-failed ERROR, 11201 set-token-failure-threw WARNING |
| 11300–11399 | Placement: `CorePlacementDirector` (`Fleans.Application.Abstractions`), `WorkerPlacementFilterDirector`, `PlacementRoleAssertion` (`Fleans.Worker`) | 11300 no core silo WARNING; 11301 no worker silo WARNING; 11310 placement-role assertion ran INFO; 11311 placement-role violation ERROR |
| 11400–11499 | `ConsistentRingSelfStatusGuard` (`Fleans.ServiceDefaults/Clustering`, #902) | 11400 ring ignored a terminating notification about the local silo while the silo was not terminating (Warning; once per affected silo start of a fresh cluster) |
| 12000–12099 | Kafka DLQ: `KafkaDlqFailureHandler`, `KafkaDeadLetterPublisher` | 12000 record dead-lettered (with failure reason) WARN; 12001 DLQ publish failed or not acknowledged within 30 s (may still be delivered) ERROR; 12002 subscription-init failure WARN; 12003 non-Kafka sequence token WARN; 12004 offset no longer retained WARN; 12005 re-consume failed ERROR; 12006 re-consume timed out WARN; 12007 re-consume client error (auth/SSL/connectivity) WARN |
| 13000–13099 | `Fleans.Web` | 13000–13004 Workflows page (13000 load failed ERROR, 13001 process started INFO, 13002 start failed WARNING, 13003 start error ERROR, 13004 enable/disable failed ERROR); 13005 `PromoteDialog` promotion failed ERROR; 13010–13019 `EnvironmentClient` (13010 promoting INFO, 13011 a call to another environment answered non-success WARNING) |
| 14100–14199 | `EfCoreWorkflowInstancePurgeService` (`Fleans.Persistence`) | Retention purge (#862): 14100 purge started, 14101 batch purged, 14102 purge finished (all Information) |
| 14200–14299 | `EditorPresenceGrain` (`Fleans.Application/Editor`) | Admin-UI editor presence: 14200 session joined, 14201 session left, 14202 session expired (all Debug) |
| 14300–14399 | `DeploymentRequestGrain` (`Fleans.Application/Deployments`) | Deploy approvals (#900): 14300 submitted, 14301 approved, 14302 rejected, 14303 withdrawn, 14304 deployed (Information); 14305 approved but the deploy failed — retry possible (Warning) |
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
