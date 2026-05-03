---
title: Observability
description: Health checks, metrics, logging, tracing, dashboards, and alerting for production Fleans deployments.
sidebar:
  order: 7
---

Fleans is built on .NET Aspire's `ServiceDefaults`, which means every silo (`Fleans.Api`, `Fleans.Web`, `Fleans.WorkerHost`, `Fleans.Mcp`) ships with the same observability primitives wired in by default: health-check endpoints, OpenTelemetry metrics + traces, and structured logging via the `[LoggerMessage]` source generator. This page documents what is emitted today and how to consume it.

## What Fleans emits today

Out of the box, every silo exposes:

- **HTTP probes** at `/health` (readiness — runs all registered health checks) and `/alive` (liveness — only checks tagged `live`). Both endpoints are anonymous so probes work even when JWT/OIDC auth is enabled.
- **OpenTelemetry metrics** from the `Microsoft.Orleans` meter plus the standard ASP.NET Core, HttpClient, and .NET runtime instrumentation.
- **OpenTelemetry traces** from the `Microsoft.Orleans.Runtime` and `Microsoft.Orleans.Application` activity sources, plus ASP.NET Core and HttpClient.
- **Structured logs** with workflow-aware scopes (workflow id, process definition, instance id) generated via `[LoggerMessage]`.

Wiring lives in [`Fleans.ServiceDefaults/Extensions.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ServiceDefaults/Extensions.cs) — `ConfigureOpenTelemetry`, `AddDefaultHealthChecks`, and `MapDefaultEndpoints`.

> **Future work — Fleans-defined Meter and ActivitySource.** Today Fleans piggy-backs on the Orleans meters and activity sources. Workflow-level metrics (workflow start rate, completion latency, active-instance gauge, custom-task duration) and Fleans-internal trace spans are not yet exported. A follow-up issue tracks adding a `Fleans` `System.Diagnostics.Metrics.Meter` and `System.Diagnostics.ActivitySource` so workflow telemetry shows up alongside the Orleans data: see the linked issue in the Fleans GitHub project.

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

`Aspire.Hosting.Kubernetes` does not yet emit these probes automatically — when you `aspire publish -t kubernetes`, you'll need to patch them onto the resulting `Deployment` manifests (or add a Kustomize overlay) before applying. See the [deploy guide][deploy] for the full publish-and-deploy flow.

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

### Useful metric families

| Source | What it measures | Example metric names (verify per-version) |
|--------|------------------|-------------------------------------------|
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
    tracing.AddAspNetCoreInstrumentation()
           .AddHttpClientInstrumentation();
});
```

What that gets you:

- **`Microsoft.Orleans.Runtime`** — internal Orleans request flow, including grain method invocation (caller silo → callee silo), and built-in resilience around grain calls.
- **`Microsoft.Orleans.Application`** — application-level grain call activity, including exceptions thrown by grain code.
- **ASP.NET Core** — incoming HTTP request spans for the API and the Web management app, with route + status code attributes.
- **HttpClient** — outbound HTTP, useful for observing the REST Caller plugin's calls.

Spans propagate W3C `traceparent` automatically — if your upstream sets a trace context (e.g. an API gateway), the resulting Fleans trace stitches into the same distributed trace.

> **Not yet traced.** Workflow-internal events such as "timer fired", "message correlated", "compensation walk advanced", "custom task plugin executed" do not produce dedicated spans today. Adding a Fleans `ActivitySource` is part of the follow-up tracked in the metrics caveat above.

## Dashboards

### Orleans Dashboard

For a quick operational view of cluster state — silos, grains, request throughput, exception counts — the [OrleansDashboard](https://github.com/OrleansContrib/OrleansDashboard) project is the easiest first step. A dedicated Fleans page covering deployment patterns is tracked in issue #402; until then, follow the upstream Orleans Dashboard docs.

### Grafana / Aspire dashboard

When you publish via `aspire publish -t kubernetes`, the silos export OTLP to whatever collector you point them at. A reasonable starter Grafana board for Fleans graphs:

- **Cluster health:** Orleans silo count over time (alert if it drops). 
- **Request volume + error rate:** ASP.NET Core `http.server.request.duration` count + 5xx rate.
- **Scheduler pressure:** Orleans `scheduler.work-item.queue.length` p95.
- **GC / memory:** `System.Runtime.gc.heap.size`, `System.Runtime.gc.collections` rate.
- **Persistence latency:** `http.client.request.duration` filtered to the Postgres / Redis hostnames, p95 / p99.
- **Outbound HTTP (REST Caller plugin):** `http.client.request.duration` filtered to non-infra hosts, error rate.

For local dev, the Aspire dashboard (`dotnet run --project Fleans.Aspire`) already shows real-time metrics, traces, and logs without any extra wiring.

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
