---
title: Observability
description: Health checks, metrics, logging, tracing, dashboards, and alerting for production Fleans deployments.
sidebar:
  order: 7
---

:::note
Looking for setup steps? See [Configuring observability](/fleans/guides/configuring-observability/).
:::

Fleans is built on .NET Aspire's `ServiceDefaults`, which means every silo (`Fleans.Api`, `Fleans.Web`, `Fleans.WorkerHost`, `Fleans.Mcp`) ships with the same observability primitives wired in by default: health-check endpoints, OpenTelemetry metrics + traces, and structured logging via the `[LoggerMessage]` source generator. This page documents what is emitted today and how to consume it.

## What Fleans emits today

Out of the box, every silo exposes:

- **HTTP probes** at `/health` (readiness — runs all registered health checks) and `/alive` (liveness — only checks tagged `live`). Both endpoints are anonymous so probes work even when JWT/OIDC auth is enabled.
- **OpenTelemetry metrics** from the `Microsoft.Orleans` and `Fleans` meters plus the standard ASP.NET Core, HttpClient, and .NET runtime instrumentation.
- **OpenTelemetry traces** from the `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Application`, and `Fleans` activity sources, plus ASP.NET Core and HttpClient.
- **Structured logs** with workflow-aware scopes (workflow id, process definition, instance id) generated via `[LoggerMessage]`.

Wiring lives in [`Fleans.ServiceDefaults/Extensions.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ServiceDefaults/Extensions.cs) — `ConfigureOpenTelemetry`, `AddDefaultHealthChecks`, and `MapDefaultEndpoints`. The Fleans-defined `Meter` and `ActivitySource` themselves live in [`Fleans.Application/Observability/FleansDiagnostics.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Application/Observability/FleansDiagnostics.cs) and are registered by name (no project reference required).

### Fleans-defined metrics

Meter name: `Fleans` (instrumentation version `1.0.0`).

| Metric | Kind | Unit | Description | Attributes |
|---|---|---|---|---|
| `fleans.workflow.started`    | Counter   | `{instances}` | Workflow instances started. | none |
| `fleans.workflow.terminated` | Counter   | `{instances}` | Workflow instances that reached a terminal state. | `result={completed,cancelled}` |
| `fleans.activity.duration`   | Histogram | `ms`          | Per-activity wall-clock duration. Explicit buckets: `[10, 50, 100, 250, 500, 1000, 5000, 10000, 30000, 60000, 300000, 600000]` ms — sub-millisecond script tasks through 10-minute REST calls. | `activity.type` |

> **Deferred to a follow-up:** `fleans.workflow.active` (a non-terminal-count gauge) requires engine-side cooperation to seed from persistence at silo startup so the count survives silo restarts. It is intentionally not shipped in this initial Meter to avoid a known-broken metric. Track via the Fleans GitHub project.

> **Best-effort caveat (`fleans.activity.duration`).** Start times are kept per-grain in memory. If a silo restarts mid-workflow, in-flight activities whose `ActivitySpawned` event landed before the restart will not record a duration on completion. The counters above are unaffected — they emit on the journaled event, not in-memory timing.

> **Activity source `Fleans` (tracing).** Declared and registered today; dedicated per-event spans (timer fired, message correlated, compensation walk advanced, custom task plugin executed) are a follow-up — plugin authors can already attach spans to `FleansDiagnostics.ActivitySource` from their own handlers.

#### Sample Prometheus alert rules

```yaml
- alert: FleansWorkflowFailureSpike
  expr: rate(fleans_workflow_terminated_total{result="cancelled"}[5m]) > 0.5
  for: 5m

- alert: FleansActivityP99Slow
  expr: histogram_quantile(0.99, rate(fleans_activity_duration_bucket[5m])) > 30000
  for: 10m
```

## Health checks

Two endpoints are mapped by `MapDefaultEndpoints`:

| Endpoint | Purpose | Probe semantics |
|----------|---------|-----------------|
| `GET /health` | Readiness — should this silo receive traffic? | Runs **every** registered health check. |
| `GET /alive`  | Liveness — is the process responsive at all? | Runs only checks tagged `live`. |

Today only a single check named `self` is registered (added in `AddDefaultHealthChecks`). It is a placeholder that always returns `Healthy()` as long as the silo's DI graph is up and the HTTP pipeline is responding. **Concretely, this means:**

- `/alive` is a true liveness probe — if the process is alive enough to answer, it returns 200.
- `/health` returns 200 as soon as the host has finished `Build()` — it does **not** yet verify Redis reachability, Postgres reachability, Orleans cluster membership, or stream provider health.

A follow-up issue tracks adding real readiness checks for the persistence database, Redis (clustering / streaming), Kafka brokers (when enabled), and Orleans silo membership state. Until then, treat `/health` as "process started" rather than "fully ready".

### Kubernetes probe example

Both probes should be wired in your Kubernetes manifest. The timing fields below are conservative defaults — tune `periodSeconds` and `failureThreshold` based on how aggressively you want the platform to evict unhealthy pods:

```yaml
livenessProbe:
  httpGet:
    path: /alive
    port: 8080
  initialDelaySeconds: 15
  periodSeconds: 10
  timeoutSeconds: 3
  failureThreshold: 3
readinessProbe:
  httpGet:
    path: /health
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10
  timeoutSeconds: 3
  failureThreshold: 3
```

The Helm chart at [`charts/fleans/`](https://github.com/nightBaker/fleans/tree/main/charts/fleans) emits these probes on every workload (`fleans-core`, `fleans-web`, `fleans-worker`, `fleans-custom-worker`, `fleans-mcp`) automatically; raw manifests extracted via `helm template` preserve them. Hand-crafted manifests should follow the snippet above.

### Adding your own health check

You can register additional checks anywhere downstream of `AddServiceDefaults`. For example, in `Fleans.Api/Program.cs`:

```csharp
builder.Services.AddHealthChecks()
    .AddRedis(builder.Configuration.GetConnectionString("redis")!, tags: ["ready"])
    .AddNpgSql(builder.Configuration.GetConnectionString("fleans")!, tags: ["ready"]);
```

Tag with `live` if a check should also gate `/alive`; otherwise it only affects `/health`.

## Metrics (OpenTelemetry)

> **Version-drift caveat.** OTel meter and metric names exposed by Orleans (and by ASP.NET Core / runtime instrumentation) are owned by the upstream Microsoft packages and **may shift between Orleans versions**. Do not hard-code dashboard queries against the names below without verifying them against the Orleans version your engine is built with — always check the live `Microsoft.Orleans.Runtime` and `Microsoft.Orleans.Application` meters at your engine version for authoritative names. The list here is illustrative.

`ConfigureOpenTelemetry` registers a single metrics pipeline that subscribes to:

- `AddAspNetCoreInstrumentation()` — incoming HTTP server metrics
- `AddHttpClientInstrumentation()` — outgoing HTTP client metrics
- `AddRuntimeInstrumentation()` — .NET runtime metrics (GC, threadpool, JIT)
- `AddMeter("Microsoft.Orleans")` — Orleans runtime metrics
- `AddMeter("Fleans")` — Fleans workflow-level metrics (see catalog above)

### Useful metric families

| Source | What it measures | Example metric names (verify per-version) |
|--------|------------------|-------------------------------------------|
| `Fleans` | Workflow lifecycle + activity duration | `fleans.workflow.started`, `fleans.workflow.terminated{result=…}`, `fleans.activity.duration{activity.type=…}` |
| `Microsoft.Orleans` | Orleans scheduler, directory, lifecycle | `orleans.scheduler.work-item.queue.length`, `orleans.directory.lookups.count`, `orleans.lifecycle.error.count` |
| `Microsoft.AspNetCore.*` | Kestrel + ASP.NET request pipeline | `Microsoft.AspNetCore.Server.Kestrel.connection.duration`, `http.server.request.duration` |
| `System.Net.Http` | Outgoing HTTP from grains and controllers | `http.client.request.duration`, `http.client.active_requests` |
| `System.Runtime` | Process / GC / threadpool | `System.Runtime.gc.heap.size`, `System.Runtime.gc.collections`, `System.Runtime.threadpool.thread.count` |

For the authoritative current list, point a local OTLP collector at a Fleans silo and inspect the metric stream — meter names are self-describing.

### Wiring an OTLP collector

`AddOpenTelemetryExporters` only enables OTLP export when the standard environment variable is set:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
# Optional, e.g. for Honeycomb / Grafana Cloud auth:
export OTEL_EXPORTER_OTLP_HEADERS="x-honeycomb-team=YOUR_KEY"
```

When `OTEL_EXPORTER_OTLP_ENDPOINT` is empty (the default), the silo skips OTLP export entirely — useful for local dev where you don't want a collector dependency. The Aspire dashboard wires its own in-memory collector for dev runs.

## Logging

Fleans uses the `[LoggerMessage]` source generator everywhere — there are no `ILogger.Log*` extension method calls in the engine. This gives:

- Zero allocation for filtered-out levels.
- Stable, source-generated `EventId`s per log message.
- Compile-time validation of message templates against parameter names.

Logging is wired through Aspire's logging pipeline (`builder.Logging.AddOpenTelemetry(...)`) so log records flow through the same OTLP exporter as traces and metrics.

### EventId ranges

Every workflow log message has a stable `EventId` in a documented range. This lets you build precise log queries that survive message-text changes. The current allocation:

| Range | Class |
|-------|-------|
| 1000-1199 | `WorkflowInstance` (sub-ranges: 1070-1079 pending events & event sub-processes, 1078 root-scope listeners, 1080-1089 complex gateway, 1090-1099 escalation, 1100-1109 transaction sub-process, 1110-1119 compensation) |
| 2000-2099 | `ActivityInstance` |
| 3000-3099 | `WorkflowInstanceState` (3030-3032 escalation warnings) |
| 4000-4099 | Event handlers |
| 5000-5099 | `WorkflowEventsPublisher` |
| 6000-6099 | `WorkflowInstanceFactoryGrain` |
| 7000-7099 | `WorkflowEngine` |
| 8000-8099 | `TimerStartEventSchedulerGrain` |
| 9000-9099 | `BpmnConverter` |
| 10000-10099 | `TimerCallbackGrain` |

The authoritative source is [`docs/plans/2026-02-08-structured-workflow-logging.md`](https://github.com/nightBaker/fleans/blob/main/docs/plans/2026-02-08-structured-workflow-logging.md) — when you add a new `[LoggerMessage]` declaration, allocate from the appropriate range and update the table there.

### Cluster homogeneity

`StreamQueueCountProbe` runs once at `ServiceLifecycleStage.Active` on every silo. It registers the silo's `(address, providerName, queueCount)` tuple in an in-memory registry grain, then cross-checks all active peers via `IManagementGrain`. Mismatches are logged at **Warning** (EventId 11300); probe errors at **Error** (EventId 11301). The check is best-effort — silo startup always continues regardless of outcome.

| EventId | Level | Meaning |
|---------|-------|---------|
| 11300 | Warning | Queue count differs between two active silos for the same provider. Streams will misroute until all silos are aligned. |
| 11301 | Error | Probe threw an exception. The silo started normally; investigate connectivity if the error recurs. |

**What to do on EventId 11300:** compare `Fleans:Streaming:Redis:TotalQueueCount`, `Fleans:Streaming:Kafka:QueueCount`, or `Fleans:Streaming:AzureQueue:QueueNames` (depending on provider) across your silo hosts. All must be identical. After fixing, restart the mismatched silo.

### Workflow-aware scopes

`WorkflowLoggingScopeFilter` (Orleans grain call filter) wraps every grain call in a `BeginScope` containing:

- `WorkflowId` (process definition logical id)
- `ProcessDefinitionKey` (deployed version key, e.g. `proc:1:abc`)
- `WorkflowInstanceId` (Guid, the instance the call belongs to)
- Activity context where applicable (`ActivityId`, `ActivityInstanceId`)

When `IncludeScopes` is enabled (`appsettings.Development.json` already does this), every log record carries those structured fields — your aggregator can pivot on them without parsing message text.

### Sample queries (aggregator-agnostic)

Filter all logs for a given workflow instance:

```text
WorkflowInstanceId = "5f9b...e7a3"
```

Filter only `ActivityInstance` events for that instance:

```text
WorkflowInstanceId = "5f9b...e7a3" AND EventId BETWEEN 2000 AND 2099
```

Find every escalation warning across the cluster:

```text
EventId BETWEEN 3030 AND 3032
```

Find every BPMN parse warning:

```text
EventId BETWEEN 9000 AND 9099 AND LogLevel = "Warning"
```

These translate to KQL, Splunk SPL, Loki LogQL, etc. — pick whatever your aggregator uses.

## Tracing

Tracing is wired in `ConfigureOpenTelemetry`:

```csharp
.WithTracing(tracing =>
{
    tracing.AddSource("Microsoft.Orleans.Runtime");
    tracing.AddSource("Microsoft.Orleans.Application");
    tracing.AddSource("Fleans");
    tracing.AddAspNetCoreInstrumentation()
           .AddHttpClientInstrumentation();
});
```

What that gets you:

- **`Microsoft.Orleans.Runtime`** — internal Orleans request flow, including grain method invocation (caller silo → callee silo), and built-in resilience around grain calls.
- **`Microsoft.Orleans.Application`** — application-level grain call activity, including exceptions thrown by grain code.
- **`Fleans`** — Fleans-defined activity source. Declared and registered today; plugin authors can attach their own spans via `FleansDiagnostics.ActivitySource`. Dedicated per-event spans for engine-internal lifecycle (timer fired, message correlated, compensation walk advanced, custom task plugin executed) are a follow-up.
- **ASP.NET Core** — incoming HTTP request spans for the API and the Web management app, with route + status code attributes.
- **HttpClient** — outbound HTTP, useful for observing the REST Caller plugin's calls.

Spans propagate W3C `traceparent` automatically — if your upstream sets a trace context (e.g. an API gateway), the resulting Fleans trace stitches into the same distributed trace.

## Dashboards

### Orleans Dashboard


The Orleans Dashboard ships with Fleans's Web app at `/dashboard`. It's a real-time operational view of the cluster (silo membership, grain activations, call latencies, reminder schedules) backed by `Microsoft.Orleans.Dashboard` 10.0.1.

#### What it shows

| Page | What you see |
|---|---|
| **Cluster** | Live silo membership table (silo id, host, status, role, version). Useful for confirming a `Core` + `Worker` split is functioning, or that a rolling restart placed each new silo into the membership table cleanly. |
| **Grains** | Activation counts per grain class. The `WorkflowInstance` row tells you how many in-flight workflow instances live across the cluster; spikes correlate with throughput surges. |
| **Reminders** | Scheduled timer reminders per grain. Useful when investigating "did my timer event sub-process actually arm?" — the reminder shows up here within seconds. Fleans uses Orleans persistent reminders for all BPMN timer events, so every timer arming is visible. |
| **Methods** | Per-method call counts, average / p99 latency, and exception counts. The `WorkflowInstance.CompleteActivity` and `…HandleMessageDelivery` rows are the high-volume paths. |

#### Accessing it in development (no auth)

When `Authentication__Authority` is empty — the default for the Compose bundle and an unconfigured Helm install — the dashboard is anonymously accessible:

```
https://localhost:<fleans-web-port>/dashboard
```

The Compose bundle binds the Web admin UI to `localhost:8080` by default — see [Deployment / Path 1](/fleans/reference/deployment/#path-1--docker-compose). For Helm installs, follow your `Ingress` host (or `kubectl port-forward svc/fleans-web 8080:8080`).

#### Accessing it under authentication

When `Authentication:Authority` is configured (production OIDC mode — see [Authentication](/fleans/reference/authentication/)), the dashboard is gated by **the same OIDC challenge as every other Web page**, despite the upstream Orleans dashboard middleware not honouring `[Authorize]`. Fleans wires an explicit middleware branch that fires *before* `MapOrleansDashboard` to enforce auth. Behavior:

1. Anonymous request → 302 → IdP login.
2. IdP callback → cookie issued.
3. Bounce to `/dashboard?<original-querystring>`.

See [Authentication § Behaviour when enabled](/fleans/reference/authentication/#behaviour-when-enabled) for the full guard.

#### Multi-replica deployments

When `Fleans.Web` runs as more than one replica, **each replica serves its own `/dashboard`**. The membership data is identical because all silos read the same Orleans cluster table from `orleans-redis`. ASP.NET Data Protection keys are persisted to `orleans-redis` so cookies issued by replica A decrypt on replica B (cookie session continuity is preserved across replicas).

#### Operational caveats

- The dashboard pulls data **from the silo it runs in**. In a Combined-role single-silo deploy, that's the only silo's view (sufficient because the `Cluster` page enumerates *all* silos via Orleans's gossip table).
- The dashboard's HTTP traffic **is** instrumented — `Fleans.ServiceDefaults` wires `AddAspNetCoreInstrumentation()` for both metrics and tracing without a path filter, so `/dashboard/*` requests appear in `http.server.request.duration` and the trace exporter alongside every other Web route. When reading the §Grafana / Aspire dashboard board described below, dashboard polling shows up under the Web-service host metrics — bear that in mind when interpreting request volume on a deployment that has frequent operator dashboard refreshes.
- For per-tenant access controls beyond "authenticated/anonymous", roll your own middleware in `Fleans.Web/Program.cs` between auth and `MapOrleansDashboard`. (Not in scope for v1.)

### Grafana / Aspire dashboard

In any production deploy (Compose bundle, Helm chart, raw `helm template` extract), the silos export OTLP to whatever collector you point them at. A reasonable starter Grafana board for Fleans graphs:

- **Cluster health:** Orleans silo count over time (alert if it drops). 
- **Request volume + error rate:** ASP.NET Core `http.server.request.duration` count + 5xx rate.
- **Scheduler pressure:** Orleans `scheduler.work-item.queue.length` p95.
- **GC / memory:** `System.Runtime.gc.heap.size`, `System.Runtime.gc.collections` rate.
- **Persistence latency:** `http.client.request.duration` filtered to the Postgres / Redis hostnames, p95 / p99.
- **Outbound HTTP (REST Caller plugin):** `http.client.request.duration` filtered to non-infra hosts, error rate.

For local dev, point the silos at any OTLP-compatible collector (e.g. the [OpenTelemetry Collector](https://opentelemetry.io/docs/collector/) running alongside the Compose stack) — no source-side wiring needed; `Fleans.ServiceDefaults`'s default OTLP exporter is on whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Alerting

The exact thresholds depend on your SLOs and traffic — the rules below are starting points to **calibrate to your workload**. Treat them as alert ideas, not prescribed numbers.

### Cluster availability

- **Silo count drops below expected.** Alert if `orleans.silo.count` < expected replica count for more than 2 minutes. Catches crashloops and partial cluster failures.
- **Lifecycle errors.** Alert if the rate of `orleans.lifecycle.error.count` over 5 minutes is greater than 0. Lifecycle errors usually indicate startup or shutdown problems that won't self-heal.

### HTTP / probe health

- **Health endpoint failing.** Alert if `/health` returns non-200 for more than 1 minute (per replica). Combined with the readiness-probe wiring in the [deploy guide][deploy], Kubernetes will already evict the pod — the alert tells humans something is structurally wrong.
- **5xx error spike.** Alert if `http.server.request.duration` count with `http.response.status_code` >= 500 exceeds 1% of total requests over 5 minutes.

### Throughput / latency

- **Scheduler backpressure.** Alert if `orleans.scheduler.work-item.queue.length` is sustained above 1000 for 5 minutes. Indicates the silo can't keep up with grain work.
- **Slow grain calls.** Alert if grain-call p99 (from the `Microsoft.Orleans.Application` activity source) exceeds your SLO for 10 minutes.
- **Persistence p99.** Alert if Postgres / Redis client p99 from `http.client.request.duration` exceeds 250 ms for 10 minutes — usually points at index regressions or connection-pool exhaustion.

### Resource saturation

- **GC pressure.** Alert if Gen2 collection rate (`System.Runtime.gc.collections`) climbs more than 3x its normal baseline.
- **Threadpool starvation.** Alert if `System.Runtime.threadpool.queue.length` is sustained above 100. Often caused by sync-over-async in custom-task plugins.

> **Calibrate, don't copy.** A 1000-item scheduler queue is alarming for a low-throughput tenant and routine for a high-throughput one. Always measure your steady-state baseline first, set thresholds at 2-3x baseline, and re-tune after each load test.

## See also

- [Self-Hosting on Kubernetes][deploy] — publish topology, replica counts, secrets
- [Persistence](/fleans/reference/persistence/) — SQLite vs PostgreSQL, migrations, query side
- [Streaming](/fleans/reference/streaming/) — Redis vs Kafka stream providers
- [Authentication](/fleans/reference/authentication/) — JWT for the API, OIDC for the Web UI
- [Load testing](/fleans/reference/load-testing/) — published throughput numbers per release

[deploy]: /fleans/reference/self-hosting/
