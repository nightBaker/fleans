# gRPC external workers

**Status:** implemented · **Date:** 2026-11-02 · **Issue:** [#979](https://github.com/nightBaker/fleans/issues/979)

## Problem

#979 added external workers through a Kafka topic per task type (`IExternalTaskQueue`,
`KafkaExternalTaskQueue`): the engine published each task of a configured type to
`fleans.tasks.<type>` and workers completed it over REST. That works, but it makes every external
worker deployment depend on a Kafka cluster even when streaming runs on Redis; the worker has to
manage offsets per partition, retry REST reports and get the at-least-once bookkeeping right on
its own; the publish runs inside the instance's turn (a broker outage fails the activity); and
the worker is invisible to the engine — no catalog entry, no editor schema.

## Decision

External tasks go to **workers connected over gRPC**. Kafka external tasks are removed. Plugins
(`CustomTaskHandlerBase` on Plugin-role silos) are unchanged: routing stays on
`Fleans:ExternalTasks:TaskTypes`, and every other type still goes to its plugin stream.

The Fleans API serves `ExternalTaskWorker`
(`Fleans.Application.Abstractions/ExternalTasks/Protos/external_task_worker.proto`, package
`fleans.externaltasks.v1`) on its HTTP endpoint. A .NET worker is a plain generic host built on
the `Fleans.ExternalTasks.Sdk` NuGet package; its only configuration is the API URL. Other
languages generate a client from the proto.

### Protocol

Server-streaming plus unary only, so it works over gRPC-Web (HTTP/1.1) as well as native gRPC:

| RPC | Purpose |
|---|---|
| `Subscribe(SubscribeRequest) → stream WorkerCommand` | Registers the worker's task types (name, display name, parameter schema) and holds the connection. Commands: `TaskAssignment`, `TaskCancellation`, `Heartbeat`. |
| `CompleteTask(task_token, result_json)` | Reports success. |
| `FailTask(task_token, code, message)` | Reports a BPMN error (`code` is the error code an error boundary catches). |

Inputs, variables and results travel as JSON. The task token (64 hex chars: activity instance id
+ dispatch id) routes the report back to the waiting dispatcher. A report for a dispatch that is
no longer waiting gets `FAILED_PRECONDITION` and is dropped by the SDK.

### Engine side

```
WorkflowInstance ──ExecuteCustomTaskEvent──▶ stream "events.ExternalTask" (key = activity instance id)
                                                  │ implicit subscription
                                                  ▼
                                   ExternalTaskDispatcherGrain ──Assign──▶ IExternalTaskWorker (observer)
                                       ▲    │ picks from                        │ WorkerConnection
                       Complete/Fail/  │    ▼                                   ▼
                       Abandon         │  ExternalTaskWorkerPoolGrain (per type) gRPC stream → worker
                                       └──────── ExternalTaskWorkerGrpcService ◀── CompleteTask / FailTask
```

- **One stream namespace** for external tasks, keyed by activity instance id like the plugin
  stream (#815), so one activation per task and parallel branches run in parallel. The dispatcher
  re-checks the activity is still active (`GetVariablesIfActive`, redelivery skip), assigns the
  task to a shuffled connected worker of the type, and awaits the outcome
  (`[AlwaysInterleave]` `Complete` / `Fail` / `Abandon`) before calling `CompleteActivity` /
  `FailActivity`. The assignment carries inputs and variables already serialized to JSON
  (`ExternalTaskJson`): stream providers may hand payload values back as
  `System.Text.Json.JsonElement` (the Redis provider does), which Orleans cannot copy to a local
  observer.
- **Any non-outcome rethrows** — no worker, worker disconnected (`Abandon`), no outcome within
  `Fleans:ExternalTasks:ExecutionTimeout` (the worker is told to cancel), grain deactivation — so
  the stream redelivers the task within `MaxEventDeliveryTime`. A task with no connected worker
  therefore waits for one instead of failing. Delivery is at-least-once; workers key side
  effects by `activity_instance_id`.
- **Worker pools** (`ExternalTaskWorkerPoolGrain`, one per task type, in memory) hold the
  connected workers' observer references as leases, renewed every `HeartbeatInterval` by the API
  process holding the worker's gRPC stream; a lease older than `WorkerTimeout` is ignored, so a
  worker behind a crashed API process stops being picked.
- **Catalog.** Workers appear in the shared custom-task catalog next to plugins:
  `CustomTaskCatalogGrain.RegisterWorker` / `UnregisterWorker` keep in-memory leases, and the
  periodic reconcile keeps a row whose name is an alive silo **or** a worker with a fresh lease.
  The worker name goes into the existing `SiloName` field, so there is no schema change.
- Any API process (Combined, Core or the REST-only `Api` role) can serve workers: the observer is
  an Orleans client object reference.
- REST `complete-activity` / `fail-activity` with `ActivityInstanceId` keep working for workers
  that report out of band.

### Configuration

Engine, `Fleans:ExternalTasks`: `TaskTypes`, `HeartbeatInterval` (15 s), `WorkerTimeout` (45 s,
must exceed the heartbeat), `ExecutionTimeout` (5 min, below the 6-min stream delivery window).
`Provider` and `Kafka:*` are gone.

Worker, `Fleans:ExternalTaskWorker`: `EngineUrl` (required), `WorkerName`, `UseGrpcWeb` (default
`true`), `AccessToken` / `AccessTokenProvider`, `MaxConcurrentTasks` (32), `ReconnectDelay` (5 s).

The gRPC endpoint is under the API's authorization fallback policy. HTTPS redirection is skipped
for `application/grpc*` requests because gRPC clients don't follow redirects.

## Alternatives considered

- **Replace plugin silos with gRPC workers too.** Prototyped (one delivery path for every custom
  task), but it removes the Plugin role, the `Fleans.Worker` package and the plugin repo's hosts
  in one step. Deferred: external tasks first, plugins can follow once the protocol has run.
- **Keep Kafka next to gRPC behind `Provider`.** Two delivery paths with different
  at-least-once contracts to document and test; rejected (KISS, pre-1.0).
- **Pull-based long-poll like Camunda external tasks.** Needs a lock/lease store for fetched
  tasks; the push stream reuses the existing durable stream for redelivery instead.
- **Bidirectional streaming.** Not supported by gRPC-Web; server-streaming plus unary covers it.

## Consequences

- Pre-1.0, no upgrade path: `Fleans:ExternalTasks:Provider` / `Kafka:*` are ignored, Kafka
  workers must be rebuilt on the gRPC protocol. No database change.
- EventIds: 14500–14599 now covers the dispatcher, pool, gRPC service and SDK (the Kafka queue's
  14510/14511 are reused); catalog 9326/9327; publisher 5002 now means "published to the
  external-task stream".
- Load stack: `tests/load/overrides/grpc-worker.yml` replaces `kafka-worker.yml`.
