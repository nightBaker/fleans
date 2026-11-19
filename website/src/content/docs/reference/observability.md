---
title: Observability
description: Health checks, metrics, logging, tracing, dashboards, and alerting for production Fleans deployments.
sidebar:
  order: 7
---

:::note
Looking for setup steps? See [Configuring observability](/fleans/guides/configuring-observability/).
:::

Fleans is built on .NET Aspire's `ServiceDefaults`, which means every Fleans host ships with the same observability primitives wired in by default: health-check endpoints, OpenTelemetry metrics + traces, and structured logging via the `[LoggerMessage]` source generator. Every host means the silos (`fleans-api` in the `Core`, `Worker` or `Combined` role, and `fleans-worker`) and the Orleans clients that join no cluster as silos (`fleans-api` in the `Api` role, `fleans-web`, `fleans-mcp`). This page documents what is emitted today and how to consume it.

## What Fleans emits today

Out of the box, every host exposes:

<figure class="arch-diagram" style="--arch-ratio: 1080 / 658">
  <iframe data-arch-src="/fleans/diagrams/observability-pipeline.html" src="/fleans/diagrams/observability-pipeline.html?embed=1" title="Observability pipeline" loading="lazy"></iframe>
  <figcaption>Metrics, traces and logs leave every host through one OTLP exporter; see Configuring observability for the wiring. <a href="/fleans/diagrams/observability-pipeline.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

- **HTTP probes** at `/health` (readiness — runs all registered health checks) and `/alive` (liveness — only checks tagged `live`). Both endpoints are anonymous so probes work even when JWT/OIDC auth is enabled.
- **OpenTelemetry metrics** from the `Microsoft.Orleans` and `Fleans` meters plus the standard ASP.NET Core, HttpClient, and .NET runtime instrumentation.
- **OpenTelemetry traces** from the `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Application`, and `Fleans` activity sources, plus ASP.NET Core and HttpClient.
- **Structured logs** with workflow-aware scope fields (see [Logs and Debugging](/fleans/reference/troubleshooting-logs-debugging/#log-scope-fields)) generated via `[LoggerMessage]`.

Wiring lives in [`Fleans.ServiceDefaults/Extensions.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ServiceDefaults/Extensions.cs) — `ConfigureOpenTelemetry`, `AddDefaultHealthChecks`, and `MapDefaultEndpoints`. The Fleans-defined `Meter` and `ActivitySource` themselves live in [`Fleans.Application.Abstractions/Observability/FleansDiagnostics.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Application.Abstractions/Observability/FleansDiagnostics.cs) and are registered by name (no project reference required).

### Fleans-defined metrics

Meter name: `Fleans` (instrumentation version `1.0.0`).

| Metric | Kind | Unit | Description | Attributes |
|---|---|---|---|---|
| `fleans.workflow.started`    | Counter   | `{instances}` | Workflow instances started. | none |
| `fleans.workflow.terminated` | Counter   | `{instances}` | Workflow instances that reached a terminal state. | `result={completed,cancelled,failed}` |
| `fleans.workflow.stalled`    | Counter   | `{deliveries}` | Activity results that did not reach the workflow instance: a custom-task handler's `FailActivity` call itself threw ("workflow may be stalled"), or the script executor gave up reporting a script's result after its retries. The activity stays executing until a redelivery succeeds (custom task) or the instance dispatches the script again after `Fleans:Worker:ScriptResultTimeout`. Counts deliveries, not instances — one stuck instance under stream redelivery counts many times. | `handler={script,custom_task}` |
| `fleans.activity.duration`   | Histogram | `ms`          | Per-activity wall-clock duration. Explicit buckets: `[10, 50, 100, 250, 500, 1000, 5000, 10000, 30000, 60000, 300000, 600000]` ms — sub-millisecond script tasks through 10-minute REST calls. | `activity.type` |
| `fleans.workflow.duration`   | Histogram | `s`           | Process duration: from the instance's start to its completed, cancelled or failed event. Buckets: `[0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 1800, 3600, 86400]` s. Its count per `result` equals `fleans.workflow.terminated`. | `result={completed,cancelled,failed}`, `process.definition.key` (the BPMN process id — one series per deployed process, never per instance or version) |
| `fleans.journal.append.duration` | Histogram | `s`       | One workflow-instance journal append — the event-store write behind every `ConfirmEvents`, retries of a failed try included. Buckets: `[0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5]` s. | `result={appended,conflict,failed}` (`conflict`: another writer's batch was there; `failed`: the store threw and Orleans retries) |
| `fleans.projection.lag`      | Histogram | `s`           | Read-model lag: from the instance publishing its `InstanceAdvanced` notification to the projector writing the projection. One sample per projection write; notifications it coalesces are not sampled. Buckets: `[0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]` s. | none |
| `fleans.timer.lag`           | Histogram | `s`           | BPMN timer lag (timer events and timer boundary events): the reminder tick that delivered the timer to its instance minus the timer's due time (Orleans `TickStatus.CurrentTickTime − FirstTickTime`). A delivery that failed or was deferred (`fleans.timer.deferred`) and was retried a minute later counts that minute. Timer **start** events are not covered. Buckets: `[0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300]` s. | none |
| `fleans.timer.deferred`      | Counter   | `{ticks}`     | BPMN timer reminder ticks not delivered because every timer delivery slot of the Core silo was busy (`Fleans:Timers:DeliveryConcurrency`, [Reminders](/fleans/reference/configuration/#reminders-timers)). The timer's reminder ticks again a minute later, so each deferral adds a minute to that timer's `fleans.timer.lag`. Zero while timers fall due slower than the cluster delivers them; a steady rate means timers are arriving faster than the silos can process them. | none |
| `fleans.kafka.consumer.lag`  | Gauge     | `{records}`   | Kafka streaming only: records past the last one this silo's stream receiver read, per topic partition, from the consumer's cached high watermark (no broker round trip). Sampled on each poll that returned records — an idle partition keeps its last value (normally 0), and a partition moved to another silo keeps its last value here until the process restarts. | `topic`, `partition` |
| `fleans.instance.migrations` | Counter  | `{instances}` | Instance migrations to another version ([Instance migration](/fleans/guides/instance-migration/)). Dry runs are not counted. | `outcome={migrated,already_migrated,rejected,failed}`, `process.definition.key` (absent for `failed` before the instance was reached) |
| `fleans.admission.limit`    | Gauge     | `{requests}`  | Current adaptive admission limit of one `Fleans.Api` process: how many REST mutations it admits at once ([Admission control](/fleans/reference/configuration/#admission-control)). It drops when the cluster slows down. | none |
| `fleans.admission.rejected` | Counter   | `{requests}`  | REST mutations answered `429` because the admission limit was reached. | none |

> **Deferred to a follow-up:** `fleans.workflow.active` (a non-terminal-count gauge) requires engine-side cooperation to seed from persistence at silo startup so the count survives silo restarts. It is intentionally not shipped in this initial Meter to avoid a known-broken metric. Track via the Fleans GitHub project.

> **`fleans.activity.duration` is measured from the event log.** The duration runs from the activity's `ActivitySpawned` event to its completed, failed or cancelled event, using the time recorded on each event. An activity that spans a silo restart or grain reactivation still records its full duration.

> **`fleans.workflow.duration` is measured from the event log, once.** The start is the instance's `WorkflowStarted` event time and the end is its terminal event's, so an instance that spans reactivations or silo restarts records its whole duration. Like the counters, it is recorded when the terminal event is raised, never when a reactivating grain replays its journal. The lag metrics (`fleans.projection.lag`, `fleans.timer.lag`) compare clocks of different silos; a negative difference from clock skew records `0`.

#### Throughput and p99 duration (the 1.0 scale metrics)

The [Road to 1.0.0](/fleans/roadmap/v1/) measures throughput as **completed instances per second** and latency as **p99 process duration**.

**Prometheus names.** The queries below assume the OpenTelemetry Collector's Prometheus exporter with its default naming: dots become underscores, counters get `_total`, and the unit is appended as a suffix — `s` → `_seconds`, `ms` → `_milliseconds`; curly-brace units such as `{instances}` add nothing. So `fleans.activity.duration` (unit `ms`) becomes `fleans_activity_duration_milliseconds_bucket`. These names were derived from the instrument definitions, not read off a running collector; check them against your collector's `/metrics` output, especially if you turned unit suffixes off or export through another path.

```promql
# completed instances per second, cluster-wide
sum(rate(fleans_workflow_terminated_total{result="completed"}[1m]))

# p99 process duration in seconds, per process
histogram_quantile(0.99, sum by (le, process_definition_key) (rate(fleans_workflow_duration_seconds_bucket{result="completed"}[5m])))

# p99 journal append, p99 projector lag, p99 timer lag
histogram_quantile(0.99, sum by (le) (rate(fleans_journal_append_duration_seconds_bucket{result="appended"}[5m])))
histogram_quantile(0.99, sum by (le) (rate(fleans_projection_lag_seconds_bucket[5m])))
histogram_quantile(0.99, sum by (le) (rate(fleans_timer_lag_seconds_bucket[5m])))

# Kafka backlog per stream topic
sum by (topic) (fleans_kafka_consumer_lag)
```

> **Activity source `Fleans` (tracing).** Declared and registered today; dedicated per-event spans (timer fired, message correlated, compensation walk advanced, custom task plugin executed) are a follow-up — plugin authors can already attach spans to `FleansDiagnostics.ActivitySource` from their own handlers.

#### Sample Prometheus alert rules

```yaml
- alert: FleansWorkflowFailureSpike
  expr: sum(rate(fleans_workflow_terminated_total{result="failed"}[5m])) > 0.5
  for: 5m

- alert: FleansWorkflowStalled
  expr: increase(fleans_workflow_stalled_total[5m]) > 0
  for: 0m

- alert: FleansActivityP99Slow
  expr: histogram_quantile(0.99, sum by (le) (rate(fleans_activity_duration_milliseconds_bucket[5m]))) > 30000
  for: 10m

- alert: FleansJournalAppendSlow
  expr: histogram_quantile(0.99, sum by (le) (rate(fleans_journal_append_duration_seconds_bucket{result="appended"}[5m]))) > 0.25
  for: 10m

- alert: FleansProjectionLagging
  expr: histogram_quantile(0.99, sum by (le) (rate(fleans_projection_lag_seconds_bucket[5m]))) > 5
  for: 10m
```

`result="failed"` counts instances that ended failed; `cancelled` instances were cancelled on purpose (API, terminate end event) and usually do not belong in a failure alert.

## Health checks

Two endpoints are mapped by `MapDefaultEndpoints`:

| Endpoint | Purpose | Probe semantics |
|----------|---------|-----------------|
| `GET /health` | Readiness — should this silo receive traffic? | Runs **every** registered health check. |
| `GET /alive`  | Liveness — is the process responsive at all? | Runs only checks tagged `live`. |

The registered checks:

| Check | Tagged `live` | Registered on | Verifies |
|---|---|---|---|
| `self` | yes | every host | The process answers HTTP. |
| `database` | no | every engine host (Api, Web, Mcp, WorkerHost) | The application database (`ConnectionStrings:fleans`) accepts a connection. |
| `journal` | no | every engine host | The journal database (`ConnectionStrings:fleans-journal`, or the application database when unset). |
| Redis | no | hosts that use a Redis-backed provider | The `orleans-redis` connection. A host with clustering, reminders and streams off Redis has no Redis check. |

**Concretely, this means:**

- `/alive` is a true liveness probe: if the process is alive enough to answer, it returns 200. A database outage does not fail it, so pods are not restarted in a loop while the database is down.
- `/health` fails while the application database (`fleans`), the journal database or Redis (when used) is unreachable, which takes the pod out of the load balancer. The query database (`fleans-query`, e.g. a read replica), and the cluster and reminder databases are **not** checked.
- `/health` does **not** check Orleans cluster membership or the stream provider (Kafka, Azure Queue). A silo that has lost its cluster is noticed by the other silos' membership probes; see [High availability](/fleans/reference/high-availability/).

### Kubernetes probe example

Both probes should be wired in your Kubernetes manifest. These are the values the Helm chart emits on its Fleans workloads; tune `periodSeconds` and `failureThreshold` based on how aggressively you want the platform to evict unhealthy pods:

```yaml
livenessProbe:
  httpGet:
    path: /alive
    port: http
  initialDelaySeconds: 30
  periodSeconds: 30
readinessProbe:
  httpGet:
    path: /health
    port: http
  initialDelaySeconds: 10
  periodSeconds: 10
```

Raw manifests extracted with `helm template` keep them; hand-crafted manifests should follow the snippet above. The [Helm guide](/fleans/guides/self-host-helm/) lists the probes per workload.

### Adding your own health check

If you build your own host on the Fleans packages, you can register additional checks anywhere downstream of `AddServiceDefaults`. For example, to also fail `/health` when the read replica is down (the built-in checks already cover `fleans`, the journal and `orleans-redis`):

```csharp
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("fleans-query")!, name: "query-database");
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

`AddOpenTelemetryExporters` enables OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set — the per-signal variables (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` and so on) do not turn it on by themselves:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
# Optional, e.g. for Honeycomb / Grafana Cloud auth:
export OTEL_EXPORTER_OTLP_HEADERS="x-honeycomb-team=YOUR_KEY"
```

When `OTEL_EXPORTER_OTLP_ENDPOINT` is empty, the host skips OTLP export entirely. The Compose bundle sets it on every Fleans service to the bundled `compose-dashboard` (an Aspire dashboard); the Helm chart does not set it. See [Configuring observability](/fleans/guides/configuring-observability/) for pointing it at your collector.

## Logging

Fleans uses the `[LoggerMessage]` source generator everywhere — there are no `ILogger.Log*` extension method calls in the engine. This gives:

- Zero allocation for filtered-out levels.
- Stable, source-generated `EventId`s per log message.
- Compile-time validation of message templates against parameter names.

Logging is wired through Aspire's logging pipeline (`builder.Logging.AddOpenTelemetry(...)`) so log records flow through the same OTLP exporter as traces and metrics.

### EventId ranges

Every workflow log message has a stable `EventId` in a range allocated per component, so log queries survive message-text changes. The full, current table is in [Logs and Debugging — EventId ranges](/fleans/reference/troubleshooting-logs-debugging/#eventid-ranges). The ranges used most in queries:

| Range | Component |
|-------|-----------|
| 1000–1199 | `WorkflowInstance` — execution, event store, conditions, scripts, migration |
| 1200–1299 | Effect handlers — timer, message, signal, user-task and child-workflow registrations |
| 3000–3099 | `WorkflowInstance` state changes (3031–3032 escalation warnings) |
| 4070–4076 | Read-model projector |
| 9000–9009 | Message correlation |
| 9020–9024 | Script executor |
| 11300–11301 | Placement fallback (no Core or no Worker silo) |
| 14500–14599 | External workers over gRPC |

### Workflow-aware scopes

Every grain call that belongs to a workflow instance runs inside a logging scope with seven fields: `WorkflowId`, `ProcessDefinitionId`, `WorkflowInstanceId`, `BusinessId`, `ActivityId`, `ActivityInstanceId` and `VariablesId` (the last two are reserved and `-` today). What each holds, and which lines lack them, is in [Logs and Debugging — Log-scope fields](/fleans/reference/troubleshooting-logs-debugging/#log-scope-fields).

`BusinessId = "ORDER-42"` finds the instance's own log lines, the grains it calls, and the script and custom-task work that runs for it. To see everything, including timer callbacks, look up the instance's `WorkflowInstanceId` once (it is in every matched line) and filter on that.

The OTLP log exporter always includes scopes (`IncludeScopes = true`), so every exported record carries those fields as attributes — your aggregator can pivot on them without parsing message text.

### Sample queries (aggregator-agnostic)

Filter all logs for a given workflow instance:

```text
WorkflowInstanceId = "5f9b...e7a3"
```

Only Warning and above for that instance:

```text
WorkflowInstanceId = "5f9b...e7a3" AND LogLevel IN ("Warning", "Error", "Critical")
```

Find every escalation warning across the cluster:

```text
EventId BETWEEN 3031 AND 3032
```

Find scripts that failed or whose result could not be reported:

```text
EventId BETWEEN 9021 AND 9024
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
- **`Fleans`** — Fleans-defined activity source. Declared and registered today; plugin authors can attach their own spans via `FleansDiagnostics.ActivitySource`. The instance migration service opens one `fleans.instance.migrate_batch` span per request (tags `fleans.migration.request_id`, `.target`, `.dry_run`, `.instances`). Dedicated per-event spans for engine-internal lifecycle (timer fired, message correlated, compensation walk advanced, custom task plugin executed) are a follow-up.
- **ASP.NET Core** — incoming HTTP request spans for the API and the Web management app, with route + status code attributes.
- **HttpClient** — outbound HTTP, useful for observing calls made by plugins (e.g. the [REST Caller plugin](https://github.com/nightBaker/fleans-plugins)) on a host that exports these traces.

Spans propagate W3C `traceparent` automatically — if your upstream sets a trace context (e.g. an API gateway), the resulting Fleans trace stitches into the same distributed trace.

## Dashboards

### Orleans Dashboard

The Orleans Dashboard is served by the Web admin UI (`fleans-web`; the service is `fleans-management` in the Compose bundle) at `/dashboard`. It's a real-time operational view of the cluster (silo membership, grain activations, call latencies, reminder schedules) backed by `Microsoft.Orleans.Dashboard` 10.4.0.

**It is only mapped in two cases**, because without authentication it would expose cluster internals to anyone who can reach the UI:

- **Web authentication is on** — both `Authentication__Authority` and `Authentication__ClientId` are set on `fleans-web` (see [Authentication](/fleans/reference/authentication/)). The dashboard then sits behind the same OIDC login as the rest of the UI.
- **The environment is `Development`** (`ASPNETCORE_ENVIRONMENT=Development` on `fleans-web`). The dashboard is then open to anyone, and the host logs a warning at startup.

Neither the Compose bundle nor the Helm chart sets either by default, so on a default install `/dashboard` returns 404. Use the [OTLP metrics](#metrics-opentelemetry) instead, or enable Web authentication.

#### What it shows

| Page | What you see |
|---|---|
| **Cluster** | Live silo membership table (silo id, host, status, role, version). Useful for confirming a `Core` + `Worker` split is functioning, or that a rolling restart placed each new silo into the membership table cleanly. |
| **Grains** | Activation counts per grain class. The `WorkflowInstance` row tells you how many in-flight workflow instances live across the cluster; spikes correlate with throughput surges. |
| **Reminders** | Scheduled timer reminders per grain. Useful when investigating "did my timer event sub-process actually arm?" — the reminder shows up here within seconds. Fleans uses Orleans persistent reminders for all BPMN timer events, so every timer arming is visible. |
| **Methods** | Per-method call counts, average / p99 latency, and exception counts. The `WorkflowInstance.CompleteActivity` and `…HandleMessageDelivery` rows are the high-volume paths. |

#### Accessing it in Development (no auth)

With `ASPNETCORE_ENVIRONMENT=Development` on `fleans-web` and Web authentication off, open:

```
http://localhost:8080/dashboard
```

The Compose bundle binds the Web admin UI to `localhost:8080` — see [Deployment / Path 1](/fleans/reference/deployment/#path-1--docker-compose). For Helm installs, follow your `Ingress` host (or `kubectl port-forward svc/<release>-web 8080:8080`). Only do this on a trusted network: Development also turns on detailed error pages.

#### Accessing it under authentication

When `Authentication:Authority` and `Authentication:ClientId` are both configured on `fleans-web` (production OIDC mode — see [Authentication](/fleans/reference/authentication/)), the dashboard is gated by **the same OIDC challenge as every other Web page**, despite the upstream Orleans dashboard middleware not honouring `[Authorize]`. Fleans wires an explicit middleware branch that fires *before* `MapOrleansDashboard` to enforce auth. Behavior:

1. Anonymous request → 302 → IdP login.
2. IdP callback → cookie issued.
3. Bounce to `/dashboard?<original-querystring>`.

See [Authentication § Behaviour when enabled](/fleans/reference/authentication/#behaviour-when-enabled) for the full guard.

#### Multi-replica deployments

When `Fleans.Web` runs as more than one replica, **each replica serves its own `/dashboard`**. The membership data is identical because every host reads the same Orleans membership table. With authentication on, ASP.NET Data Protection keys are persisted to `orleans-redis` **only when the Web host uses Redis** (its clustering provider is Redis), so cookies issued by replica A decrypt on replica B. With membership on Postgres the keys stay in each pod: run a single Web replica, or a sticky-session ingress.

#### Operational caveats

- `fleans-web` is an Orleans client, not a silo: the dashboard shows what every silo collects (each silo registers the dashboard's collector), so one Web replica sees the whole cluster.
- Any signed-in user can open it — the gate checks sign-in, not a [role](/fleans/reference/authorization/).
- The dashboard's HTTP traffic **is** instrumented — `Fleans.ServiceDefaults` wires `AddAspNetCoreInstrumentation()` for both metrics and tracing without a path filter, so `/dashboard/*` requests appear in `http.server.request.duration` and the trace exporter alongside every other Web route. When reading the §Grafana / Aspire dashboard board described below, dashboard polling shows up under the Web-service host metrics — bear that in mind when interpreting request volume on a deployment that has frequent operator dashboard refreshes.

### Grafana / Aspire dashboard

In any production deploy (Compose bundle, Helm chart, raw `helm template` extract), the hosts export OTLP to whatever collector you point them at. A reasonable starter Grafana board for Fleans graphs:

- **Cluster health:** Orleans silo count over time (alert if it drops). 
- **Request volume + error rate:** ASP.NET Core `http.server.request.duration` count + 5xx rate.
- **Scheduler pressure:** Orleans `scheduler.work-item.queue.length` p95.
- **GC / memory:** `System.Runtime.gc.heap.size`, `System.Runtime.gc.collections` rate.
- **Persistence latency:** `fleans.journal.append.duration` p95 / p99 (the event-store write behind every workflow step) and `fleans.projection.lag` p99 (how far the read model trails). Fleans registers no PostgreSQL or Redis client instrumentation, so database calls do not show up in `http.client.*`.
- **Outbound HTTP:** `http.client.request.duration`, error rate — on hosts that make HTTP calls (e.g. the admin UI's environment promotion or AI assistant). Custom-task workers run outside the cluster and export their own telemetry, if any.

Point the hosts at any OTLP-compatible collector (e.g. the [OpenTelemetry Collector](https://opentelemetry.io/docs/collector/) running alongside the Compose stack) — no source-side wiring needed; the OTLP exporter is on whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

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
- **Journal append p99.** Alert if `fleans.journal.append.duration{result="appended"}` p99 exceeds 250 ms for 10 minutes — usually points at a saturated journal database or connection-pool exhaustion. A rising `result="failed"` rate means the store is throwing.
- **Read-model lag.** Alert if `fleans.projection.lag` p99 exceeds a few seconds for 10 minutes — lists in the admin UI and `GET /Instances` fall behind.

### Resource saturation

- **GC pressure.** Alert if Gen2 collection rate (`System.Runtime.gc.collections`) climbs more than 3x its normal baseline.
- **Threadpool starvation.** Alert if `System.Runtime.threadpool.queue.length` is sustained above 100. Often caused by sync-over-async in custom-task plugins.

> **Calibrate, don't copy.** A 1000-item scheduler queue is alarming for a low-throughput tenant and routine for a high-throughput one. Always measure your steady-state baseline first, set thresholds at 2-3x baseline, and re-tune after each load test.

## See also

- [Helm Chart][deploy] — topology, replica counts, secrets
- [Persistence](/fleans/reference/persistence/) — SQLite vs PostgreSQL, migrations, query side
- [Streaming](/fleans/reference/streaming/) — Redis vs Kafka stream providers
- [Authentication](/fleans/reference/authentication/) — JWT for the API, OIDC for the Web UI
- [Logs and Debugging](/fleans/reference/troubleshooting-logs-debugging/) — EventId ranges and log-scope fields
- [Load testing](/fleans/reference/load-testing/) — measured throughput and the load-test stack

[deploy]: /fleans/guides/self-host-helm/
