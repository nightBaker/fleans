# Observability EventId ranges

Contributor-facing reference for the structured-logging EventId allocation. Allocate new
`[LoggerMessage]` EventIds from the appropriate range below. New ranges must be added
here when a new class starts emitting structured logs.

**Every EventId names one event.** `LoggerEventIdUniquenessTests` (`Fleans.Application.Tests`)
scans `src/Fleans/**/*.cs` and fails when two `[LoggerMessage]` declarations share an EventId
— alerts keyed on an EventId must not fire on an unrelated event. The only exceptions are
listed in the test's `SharedEventIds` (today 11100: one event logged from three sibling Kafka
classes); add to it only for the same event, and note it here.

**No `ILogger.Log*()` calls.** The same test class fails on any `.LogInformation(` / `.LogError(` /
… call in non-test sources (`.cs` and `.razor`); only top-level `Program.cs` startup messages are
exempt. In a Razor page, inject the logger in a `.razor.cs` partial (`[Inject] ILogger<T> Logger`) —
the `[LoggerMessage]` generator can't see members that `@inject` generates (pattern: `Workflows.razor.cs`).

For the architectural background (filter pattern, scope-field semantics, RequestContext
propagation), see [`docs/plans/2026-02-08-structured-workflow-logging.md`](../plans/2026-02-08-structured-workflow-logging.md).

## Allocated ranges

| Range | Class | Notes |
|-------|-------|-------|
| 1000–1199 | `WorkflowInstance` | Sub-ranges: 1066 UserTaskClaim rejection (Warning); 1070–1079 pending events / event sub-processes; 1078 root-scope listeners; 1080–1089 complex gateway; 1090–1099 escalation; 1100–1109 transaction sub-process; 1110–1119 compensation; 1150–1153 multiple events (1150 multiple catch registered, 1151 multiple catch fired, 1152 multiple throw fired, 1153 multiple boundary fired); 1120 workflow failed by unhandled activity failure (Error, #762); 1131–1139 event store (1131 snapshot write failed (Error), 1132 `InstanceAdvanced` publish failed — retrying, 1133 read-model row differs from the journal on activation — projector asked again, 1134 publish succeeded after retry, 1135 publish retries gave up (Error), 1136 read-model row read failed, 1137 journal moved during a failed or conflicting append — activation retired (Error), 1138 append failed, journal unchanged — live state kept (Warning), 1139 append failed but the journal moved — retried with the same batch id, which the store confirms (Warning, #877)); 1140 variables over `Fleans:Variables:MaxSizeBytes` rejected (Warning); 1141 state snapshot served without its process definition (Warning); 1142 pending event journaled and queued (Debug); 1147 repeated start of an already-started instance ignored (#863); 1148 terminal instance deactivating once idle (Debug, #860); 1160–1169 in-process conditions and script dispatch (1160 gateway condition skipped — gateway no longer active (Debug), 1161 condition evaluation failed — activity failed (Error), 1162 completion condition failed — log-only (Error), 1163 completion condition result (Debug), 1164 script dispatched (Debug), 1165 no script result in time — dispatched again (Warning), 1166 no script result after the last dispatch — task failed (Error), 1167 activation dispatched executing scripts again (Information), 1168 that re-dispatch failed (Error), 1169 custom task not handed to its plugin stream or external-task queue — activity failed (Error, #979)) |
| 1200–1299 | Effect handlers (`Fleans.Application/Effects/*`) | 1200–1209 `WorkflowLifecycleEffectHandler` (1200 starting child workflow, 1201 child start failed ERROR); 1210–1219 `TimerEffectHandler` (1210 reminder registered, 1211 reminder registration failed WARNING, 1212 reminder unregistered, 1213 unregister failed WARNING); 1220–1229 `MessageEffectHandler` (1220 subscribed, 1221 subscribe failed WARNING, 1222 unsubscribe failed WARNING); 1230–1239 `SignalEffectHandler` (1230 subscribed, 1231 subscribe failed WARNING, 1232 signal thrown, 1233 unsubscribe failed WARNING, 1234 throw failed in N buckets WARNING); 1240–1249 `UserTaskEffectHandler` (1240 registration failed, 1241 completion persistence failed, 1242 claim update failed, 1243 discard after failed registration failed — all WARNING); 1250 `EffectDispatcher` several handlers match one effect — first one used WARNING |
| 2000–2099 | `ActivityInstance` | |
| 3000–3099 | `WorkflowInstanceState` | Includes 3030–3032 escalation warnings; 3035 activity input scope opened by `fleans:ioMapping` (Debug, #948) |
| 4000–4099 | Event handlers | `Fleans.Application/Events/Handlers/*`; `CustomTaskHandlerBase` (`Fleans.Worker`) uses 4030–4035, 4037–4039, 4050: 4030 handling INFO, 4031 execute failed ERROR, 4032 stream completed INFO, 4033 stream error ERROR, 4034 FailActivity failed CRITICAL, 4035 activated INFO, 4037 task-type mismatch WARNING, 4038 skipped finished activity INFO, 4039 transient grain-call failure rethrown WARNING, 4050 cancelled on deactivation INFO. Retired with their handlers (scripts and conditions no longer use streams; don't reuse): 4000–4029, 4060–4066; 4070–4079 `WorkflowInstanceProjector` (`Fleans.Application/Projections/`, #861): 4070 projected (Debug), 4071 notification skipped — already projected (Debug), 4072 projection failed, redelivered (Warning), 4073 stream error (Error), 4074 notification for a purged instance (no journal) skipped (Information); `ProjectionBatcher`: 4075 batch written (Debug), 4076 batch failed, every projection in it redelivered (Warning). |
| 4100–4199 | `UserTaskGrain` | 4100 registered, 4101 claim updated, 4102 completed, 4103 discarded (all Information) |
| 5000–5099 | `WorkflowEventsPublisher` | 5000 publishing (Debug), 5001 unhandled event type dropped (Warning), 5002 external task published to the external-task queue (Debug, #979) |
| 6000–6099 | `ProcessDefinitionGrain` (6000–6049), `WorkflowDefinitionCache` (6050–6059) | 6006 hosts not told that start events changed — their caches refresh on expiry (Warning); 6050 definition loaded into the silo cache (Debug) |
| 7000–7099 | `WorkflowCommandService` | 7020 deployment request submitted (#900); 7021 completing an activity instance, 7022 failing an activity instance with an error code (both Information, #979) |
| 8000–8099 | `TimerStartEventSchedulerGrain` (8000–8003, 8011 cycle re-armed, 8012 fire skipped — process disabled), `UserTasksController` (8004–8010) | 8009 body-UserId/token mismatch rejection (Warning); 8010 token without user-id claim (Warning) |
| 8100–8199 | `GlobalExceptionHandler` (`Fleans.Api`) | |
| 8200–8299 | `DeploymentsController` (`Fleans.Api`) | 8200 body-UserId/token mismatch rejection, 8201 token without user-id claim (both Warning) |
| 8300–8399 | `AdaptiveConcurrencyLimiter` (`Fleans.Api/Admission`, #922) | 8300 admission limit lowered (Warning), 8301 limit raised (Debug), 8302 request rejected with 429 (Debug) |
| 9000–9099 | `MessageCorrelationGrain` (9000–9009), `BpmnConverter` (9010–9019), `ScriptExecutorGrain` (`Fleans.Worker`, 9020–9029) | 9010 activation condition ignored on a fork (Warning); 9020 executing (Debug), 9021 script failed (Error), 9022 result rejected by the instance — failing the activity (Warning), 9023 callback failed transiently — retrying (Warning), 9024 callback gave up — the instance re-dispatches (Critical) |
| 9100–9199 | `MessageStartEventListenerGrain` (9100–9109, registrations only), `SignalCorrelationGrain` (9110–9119, one per signal bucket; full — 9118 subscriptions restored after transient delivery failures WARNING, 9119 restore write failed ERROR) | |
| 9200–9299 | `SignalStartEventListenerGrain` (registrations only) | |
| 9300–9339 | `ConditionalStartEventListenerGrain` (9300–9309), `ConditionalStartEventRegistryGrain` (9310–9319), `CustomTaskCatalogGrain` (9320–9329), `CustomTaskPluginRegistrar` (`Fleans.Worker`, 9330–9339) | |
| 9340–9349 | *Reserved:* `RestCallerHandler` (`Fleans.Plugins.RestCaller`), now in [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) | Owned by that repository; don't reuse here. 9340 sending, 9341 received, 9342 timeout, 9343 network error, 9344 non-success status, 9345 idempotency key, 9346 JSON parse failed, 9347 response body over limit (Warning) |
| 9350–9369 | *Reserved:* Camunda external-task plugin (`Fleans.Plugins.CamundaExternalTask`) in [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins) | Allocated in that repository; don't reuse here so its logs stay unambiguous in a shared cluster. |
| 9370–9399 | `StartEventLauncher` (9370–9384), `StartEventSubscriptionCache` (9385–9389), `StartEventSubscriptionNotifierGrain` (9390–9399) (#858) | 9370 message/signal start fired, 9371 nothing subscribed (Warning), 9372 start failed (Error), 9373 disabled process skipped (Warning), 9374 more subscribed processes than the threshold — started in batches (Warning), 9375 latest version no longer has the start event (Debug), 9376–9380 conditional starts (9376 fired, 9377 condition false (Debug), 9378 disabled process skipped (Warning), 9379 latest version no longer has the conditional start (Debug), 9380 evaluation failed (Error)); 9385 cache cleared (Debug), 9386 cache could not subscribe to changes (Warning), 9387 cache passed its name limit and was cleared (Warning); 9390 host observer subscribed (Debug), 9391 hosts notified, 9392 a host dropped (Warning) |
| 10000–10099 | `TimerCallbackGrain` | |
| 11000–11099 | `KafkaProductionPresetExtensions` | 11000 preset-applied INFO |
| 11100–11199 | `KafkaQueueAdapter`, `KafkaQueueAdapterReceiver`, `KafkaQueueAdapterFactory` | 11100 SSL-no-paths OS-trust-store WARNING (shared EventId across all three sibling classes); 11101 acks-not-all WARNING; 11102 single-broker RF fallback INFO; 11103 RF>brokers fallback WARNING; 11104 GetMetadata error ERROR; 11105 topics created INFO; 11106 topics already existed INFO; 11107 record deserialization failed, skipped (DLQ off) ERROR; 11108 consumer error WARNING; 11109 consume failed — partitions released and assigned again after the lookup backoff WARNING; 11110 offset commit failed WARNING; 11111 consumer close failed WARNING; 11112 DLQ enabled but adapter not created yet, handler falls back to no-op WARNING; 11113 record deserialization failed, routing to DLQ ERROR; 11114 batch publish failed ERROR; 11115 receiver started reading new partitions of its topic DEBUG; 11116 receiver partition lookup failed, retrying with backoff WARNING; 11117 unassign after a failed consume failed WARNING |
| 11200–11299 | `MskIamTokenRefresher` | 11200 token-refresh-failed ERROR, 11201 set-token-failure-threw WARNING |
| 11300–11399 | Placement: `CorePlacementDirector` (`Fleans.Application.Abstractions`), `WorkerPlacementFilterDirector`, `PlacementRoleAssertion` (`Fleans.Worker`) | 11300 no core silo WARNING; 11301 no worker silo WARNING; 11310 placement-role assertion ran INFO; 11311 placement-role violation ERROR |
| 11400–11499 | `ConsistentRingSelfStatusGuard` (`Fleans.ServiceDefaults/Clustering`, #902) | 11400 ring ignored a terminating notification about the local silo while the silo was not terminating (Warning; once per affected silo start of a fresh cluster) |
| 12000–12099 | Kafka DLQ: `KafkaDlqFailureHandler`, `KafkaDeadLetterPublisher` | 12000 record dead-lettered (with failure reason) WARN; 12001 DLQ publish failed or not acknowledged within 30 s (may still be delivered) ERROR; 12002 subscription-init failure WARN; 12003 non-Kafka sequence token WARN; 12004 offset no longer retained WARN; 12005 re-consume failed ERROR; 12006 re-consume timed out WARN; 12007 re-consume client error (auth/SSL/connectivity) WARN |
| 13000–13099 | `Fleans.Web` | 13000–13004 Workflows page (13000 load failed ERROR, 13001 process started INFO, 13002 start failed WARNING, 13003 start error ERROR, 13004 enable/disable failed ERROR); 13005 `PromoteDialog` promotion failed ERROR; 13010–13019 `EnvironmentClient` (13010 promoting INFO, 13011 a call to another environment answered non-success WARNING); 13020–13039 `Editor` (13020–13026 import / download / prepare-deploy / deploy / request-deploy / load-by-key / load-tab failed ERROR, 13027 XML snapshot failed WARNING, 13028 tabs localStorage write rejected DEBUG, 13029 persist tabs failed WARNING, 13030 restore tabs failed WARNING, 13031 dirty-tabs interop failed DEBUG, 13032 simulation start failed ERROR, 13033 load versions failed ERROR, 13034 compare failed ERROR, 13035 presence heartbeat failed DEBUG, 13036 presence leave failed DEBUG); 13040–13049 `ProcessInstance` (13040 load ERROR, 13041 compensation log load failed WARNING, 13042 journal replay stopped DEBUG, 13043 refresh ERROR); 13050 `ProcessInstances` load ERROR; 13055 `InstanceDebugger` journal read failed WARNING; 13060–13069 `Deployments` (13060 load ERROR, 13061 request action failed WARNING) |
| 14100–14199 | `EfCoreWorkflowInstancePurgeService` (`Fleans.Persistence`) | Retention purge (#862): 14100 purge started, 14101 batch purged, 14102 purge finished, 14103 expired message idempotency records purged (#977) (all Information) |
| 14200–14299 | `EditorPresenceGrain` (`Fleans.Application/Editor`) | Admin-UI editor presence: 14200 session joined, 14201 session left, 14202 session expired (all Debug) |
| 14300–14399 | `DeploymentRequestGrain` (`Fleans.Application/Deployments`) | Deploy approvals (#900): 14300 submitted, 14301 approved, 14302 rejected, 14303 withdrawn, 14304 deployed (Information); 14305 approved but the deploy failed — retry possible (Warning) |
| 14400–14499 | `MessageDeduplicationGrain` (`Fleans.Application/Grains`) | Message sends with an idempotency key (#743): 14400 repeat answered from the record (Information), 14401 correlated delivery recorded, 14402 starts planned, 14403 starts recorded, 14405 nothing started — record dropped (Debug); 14404 interrupted send resumed with the planned ids (Warning) |
| 14500–14599 | External tasks (#979): `KafkaExternalTaskQueue` (`Fleans.Infrastructure/ExternalTasks`, 14510–14519) | 14510 task topics created (Information), 14511 producer error (Warning) |
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
