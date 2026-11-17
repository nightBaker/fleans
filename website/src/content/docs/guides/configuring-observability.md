---
title: Configuring observability
description: Wire Fleans's built-in OpenTelemetry metrics, traces, and logs into Prometheus, Tempo/Jaeger, and Loki.
---

Every Fleans host emits OpenTelemetry metrics, traces, and structured logs out of the box — no
instrumentation code required. This guide wires all three signals into your observability stack
using the standard OTLP pipeline.

For a catalog of *what* Fleans emits (metric names, EventId ranges, trace sources, alert rules),
see [Observability reference](/fleans/reference/observability/).

## What you get out of the box

Every Fleans host — the silos (`fleans-api` in a silo role, `fleans-worker`) and the Orleans
clients (`fleans-api` with `Fleans__Role=Api`, `fleans-web`, `fleans-mcp`) — ships with the
following signals auto-wired via `Fleans.ServiceDefaults`:

<figure class="arch-diagram" style="--arch-ratio: 1080 / 658">
  <iframe data-arch-src="/fleans/diagrams/observability-pipeline.html" src="/fleans/diagrams/observability-pipeline.html?embed=1" title="Observability pipeline" loading="lazy"></iframe>
  <figcaption>Every host emits metrics, traces and logs through one OTLP exporter (enabled by <code>OTEL_EXPORTER_OTLP_ENDPOINT</code>) to a collector or a hosted backend; dev runs use the Aspire dashboard. <a href="/fleans/diagrams/observability-pipeline.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

| Signal | Source names |
|--------|-------------|
| **Metrics** | `Fleans`, `Microsoft.Orleans`, `Microsoft.AspNetCore.*`, `System.Net.Http`, .NET runtime |
| **Traces** | `Fleans`, `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Application`, ASP.NET Core, HttpClient |
| **Logs** | Structured logs with workflow-aware [scope fields](/fleans/reference/troubleshooting-logs-debugging/#log-scope-fields) (`WorkflowId`, `ProcessDefinitionId`, `WorkflowInstanceId`, `BusinessId`, `ActivityId`, …), every entry carrying an EventId |

The only exporter wired out of the box is OTLP, and it is switched on by
`OTEL_EXPORTER_OTLP_ENDPOINT` alone. When that variable is unset, no telemetry is exported —
the signals are collected but silently discarded. The Compose bundle sets it on every Fleans
service to its bundled `compose-dashboard` (an Aspire dashboard, for a quick look); the Helm
chart sets nothing.

## Path 1 — OTLP Collector (recommended)

The OpenTelemetry Collector acts as a single ingestion point. It receives all three signals
from the silos and fans them out to Prometheus (metrics), Jaeger/Tempo (traces), and Loki (logs).

### 1. Configure the silo containers

Set these environment variables on every Fleans container:

```bash
# Required — collector address. Nothing is exported without it.
OTEL_EXPORTER_OTLP_ENDPOINT=http://otelcol:4317

# Protocol: grpc (default) | http/protobuf (preferred for NAT/proxies)
OTEL_EXPORTER_OTLP_PROTOCOL=grpc

# Service name shown in Tempo/Jaeger/Loki — one per kind of host
OTEL_SERVICE_NAME=fleans-core
```

Give each kind of host its own `OTEL_SERVICE_NAME` (`fleans-core`, `fleans-worker`,
`fleans-api`, `fleans-web`, `fleans-mcp`) so you can tell their signals apart.

**Docker Compose bundle.** The OTEL variables are written into `docker-compose.yaml` itself
(services `fleans-core`, `fleans-management`, `fleans-worker`, `fleans-mcp`), not read from
`.env`, so setting them in `.env` changes nothing. Override them per service in a
`docker-compose.override.yaml` next to the bundle, which `docker compose` merges automatically:

```yaml
services:
  fleans-core:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otelcol:4317"
  fleans-management:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otelcol:4317"
  fleans-worker:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otelcol:4317"
  fleans-mcp:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otelcol:4317"
```

The bundle already sets `OTEL_SERVICE_NAME` per service. The collector must be on the bundle's
`aspire` network.

**Helm.** Use the chart's `extraEnv` list, which is added to the core, worker, api, web and mcp
workloads (not the `plugins` worker):

```yaml
extraEnv:
  - name: OTEL_EXPORTER_OTLP_ENDPOINT
    value: "http://otelcol.observability:4317"
  - name: OTEL_EXPORTER_OTLP_PROTOCOL
    value: "grpc"
```

`extraEnv` is shared by all workloads, so it cannot set a different `OTEL_SERVICE_NAME` per
workload; the `k8s.*` resource attributes your collector adds (for example with the
`k8sattributes` processor) tell the pods apart.

If your metrics, traces, and logs backends have different endpoints, you can add the
per-signal overrides (`OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, `…_TRACES_ENDPOINT`,
`…_LOGS_ENDPOINT`) — **in addition to** `OTEL_EXPORTER_OTLP_ENDPOINT`, which must still be set
because it is what turns the exporter on. Routing per signal in the collector is usually
simpler.

### 2. Deploy a minimal OpenTelemetry Collector

Save the following as `otelcol-config.yaml` and deploy it alongside your stack:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:

exporters:
  prometheus:
    endpoint: 0.0.0.0:8889   # Prometheus scrapes this
  otlp/jaeger:
    endpoint: jaeger:4317     # or tempo:4317 for Grafana Tempo
    tls:
      insecure: true
  loki:
    endpoint: http://loki:3100/loki/api/v1/push

service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheus]
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlp/jaeger]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [loki]
```

Add a `prometheus.yml` scrape config pointing at the collector's `/metrics` endpoint:

```yaml
scrape_configs:
  - job_name: fleans
    static_configs:
      - targets: ['otelcol:8889']
```

### 3. Verify signals are flowing

**Metrics** — trigger a workflow start (see the [REST API reference](/fleans/reference/api/)),
then query the collector's Prometheus endpoint:

```bash
curl -s http://otelcol:8889/metrics | grep fleans_workflow_started
```

You should see `fleans_workflow_started_total` (OTel SDK ≥ 1.7) or `fleans_workflow_started`
(older SDK versions). Both indicate the counter is flowing correctly.

**Traces** — in Jaeger or Tempo, search for service `fleans-core` (or whichever service name
you set). You should see spans for grain calls and HTTP requests.

**Logs** — in Loki (or Grafana Explore), query `{service_name="fleans-core"}`. Workflow
execution logs carry a structured `WorkflowInstanceId` field you can filter on:
`{service_name="fleans-core"} | json | WorkflowInstanceId != ""`.

:::note
**Kafka deployments**: each silo reports its stream receivers' backlog as the
`fleans_kafka_consumer_lag` gauge (records, per `topic` and `partition`) — see the
[metric catalog](/fleans/reference/observability/#fleans-defined-metrics).
:::

## Note: Direct Prometheus pull

A direct `/metrics` scrape endpoint on the silo container requires adding the
`OpenTelemetry.Exporter.Prometheus.AspNetCore` NuGet package, which is not included in current
Fleans releases. For metrics without a separate collector, use Path 1 with a single-node
OpenTelemetry Collector (`otelcol/opentelemetry-collector-contrib`) pointing at a local
Prometheus instance.

## Path 2 — Grafana Cloud (managed OTLP)

Grafana Cloud provides a managed OTLP endpoint that accepts all three signals. Set these
environment variables on the Fleans containers instead of the collector address:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-<region>.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64(instanceId:apiKey)>
```

Replace `<region>` with your Grafana Cloud stack region (e.g. `prod-eu-west-0`) and
`<base64(instanceId:apiKey)>` with the base64-encoded `instanceId:apiKey` from your Grafana
Cloud OTLP connection settings.

:::note
If your API key contains `+` or `=` characters, URL-encode the value of the `Authorization`
header: `Authorization=Basic%20<encoded-token>`.
:::

No OpenTelemetry Collector is needed — signals go directly to Grafana Cloud's ingestion layer
and appear in Prometheus, Tempo, and Loki within a few seconds.

## Common alerts

The [Observability reference](/fleans/reference/observability/#sample-prometheus-alert-rules) has
sample Prometheus alert rules (failed instances, stalled deliveries, slow activities, journal
append latency, read-model lag) and [alert ideas](/fleans/reference/observability/#alerting) for
silo availability and resource saturation. Check the metric names against your collector's
`/metrics` output before copying them into your `rules.yaml`.

## Troubleshooting

**I see `Microsoft.Orleans.*` metrics but no `fleans_*` metrics**

The `Fleans` meter is not subscribed in the collector or your OTel SDK configuration. Verify
that the `OTEL_EXPORTER_OTLP_ENDPOINT` is set and that no metric filter is dropping the
`Fleans` meter. In `Fleans.ServiceDefaults/Extensions.cs`, the `Fleans` meter is registered
via `AddMeter("Fleans")` — it's always present; the issue is usually a misconfigured exporter
or network path to the collector.

**No traces appearing in Tempo/Jaeger**

Check that `OTEL_EXPORTER_OTLP_ENDPOINT` is set and resolves from the container (a
per-signal `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` alone does not turn the exporter on). Confirm the `Fleans` activity source is wired: `Fleans.ServiceDefaults` registers it
via `AddSource("Fleans")`. If spans appear for `Microsoft.Orleans.*` but not `Fleans`, a
sampling rule may be dropping the `Fleans` activity source specifically.

**Logs missing `WorkflowInstanceId` in Loki**

The OTLP log exporter must be configured to capture structured key-value properties, not just
the formatted message string. In the OTel Collector, the default `otlplogs` or `loki` exporter
propagates structured attributes. Confirm your Loki pipeline uses `| json` in queries to
unpack the attributes from the OTLP body.

**All signals absent after configuration**

Restart the silo containers after adding `OTEL_EXPORTER_OTLP_ENDPOINT` — the variable is read
at startup. Verify the collector is reachable from the containers' network: the Fleans images
have no `curl`, so test from a throwaway container on the same network, e.g.
`docker run --rm --network <network> curlimages/curl -s http://otelcol:4317` — a protocol error
is fine (the port speaks gRPC); a timeout or connection refused is not.

For additional diagnostics, see [Common issues](/fleans/reference/troubleshooting-common-issues/)
and [Logs and Debugging](/fleans/reference/troubleshooting-logs-debugging/).
