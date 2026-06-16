---
title: Configuring observability
description: Wire Fleans's built-in OpenTelemetry metrics, traces, and logs into Prometheus, Tempo/Jaeger, and Loki.
---

Every Fleans silo emits OpenTelemetry metrics, traces, and structured logs out of the box — no
instrumentation code required. This guide wires all three signals into your observability stack
using the standard OTLP pipeline.

For a catalog of *what* Fleans emits (metric names, EventId ranges, trace sources, alert rules),
see [Observability reference](/fleans/reference/observability/).

## What you get out of the box

Every silo (`fleans-api`, `fleans-web`, `fleans-worker`, `fleans-mcp`) ships with the following
signals auto-wired via `Fleans.ServiceDefaults`:

| Signal | Source names |
|--------|-------------|
| **Metrics** | `Fleans`, `Microsoft.Orleans`, `Microsoft.AspNetCore.*`, `System.Net.Http`, .NET runtime |
| **Traces** | `Fleans`, `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Application`, ASP.NET Core, HttpClient |
| **Logs** | Structured logs with workflow-aware scopes (`WorkflowId`, `ProcessDefinitionId`, `InstanceId`), every entry carrying an EventId |

The only exporter wired out of the box is OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`). When that
variable is unset, no telemetry is exported — the signals are collected but silently discarded.

## Path 1 — OTLP Collector (recommended)

The OpenTelemetry Collector acts as a single ingestion point. It receives all three signals
from the silos and fans them out to Prometheus (metrics), Jaeger/Tempo (traces), and Loki (logs).

### 1. Configure the silo containers

Add these environment variables to every Fleans container (`fleans-api`, `fleans-web`,
`fleans-worker`, `fleans-mcp`):

```bash
# Required — collector address
OTEL_EXPORTER_OTLP_ENDPOINT=http://otelcol:4317

# Protocol: grpc (default) | http/protobuf (preferred for NAT/proxies) | http/json
OTEL_EXPORTER_OTLP_PROTOCOL=grpc

# Service name shown in Tempo/Jaeger/Loki
OTEL_SERVICE_NAME=fleans-silo
```

For **Docker Compose** deployments, add these to the `.env` file next to your `compose.yaml`.
For **Helm** deployments, set them under `env:` in your `values.yaml` override.

If your metrics, traces, and logs backends have different endpoints, use the per-signal overrides:

```bash
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://otelcol:4317
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=http://tempo:4317
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://loki-otlp:4317
```

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

**Traces** — in Jaeger or Tempo, search for service `fleans-silo`. You should see spans for
the silo's grain activations and HTTP requests.

**Logs** — in Loki (or Grafana Explore), query `{service_name="fleans-silo"}`. Workflow
execution logs carry a structured `WorkflowId` field you can filter on:
`{service_name="fleans-silo"} | json | WorkflowId != ""`.

:::note
**Kafka deployments**: once [#687](https://github.com/nightBaker/fleans/issues/687)
(consumer-lag metrics) ships, look for additional `fleans_kafka_consumer_lag_*` metrics
in the Prometheus output. This guide will be updated when that lands.
:::

## Note: Direct Prometheus pull

A direct `/metrics` scrape endpoint on the silo container requires adding the
`OpenTelemetry.Exporter.Prometheus.AspNetCore` NuGet package, which is not included in current
Fleans releases. For metrics without a separate collector, use Path 1 with a single-node
OpenTelemetry Collector (`otelcol/opentelemetry-collector-contrib`) pointing at a local
Prometheus instance.

## Path 2 — Grafana Cloud (managed OTLP)

Grafana Cloud provides a managed OTLP endpoint that accepts all three signals. Set these
environment variables on the silo containers instead of the collector address:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-<region>.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64(instanceId:apiKey)>
OTEL_SERVICE_NAME=fleans-silo
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

The [Observability reference](/fleans/reference/observability/#alerting-recipes) has ready-to-use
Prometheus alert rules for workflow throughput, silo availability, activity failure rate, and
resource saturation. Copy the rules into your `rules.yaml` once you confirm metrics are flowing.

## Troubleshooting

**I see `Microsoft.Orleans.*` metrics but no `fleans_*` metrics**

The `Fleans` meter is not subscribed in the collector or your OTel SDK configuration. Verify
that the `OTEL_EXPORTER_OTLP_ENDPOINT` is set and that no metric filter is dropping the
`Fleans` meter. In `Fleans.ServiceDefaults/Extensions.cs`, the `Fleans` meter is registered
via `AddMeter("Fleans")` — it's always present; the issue is usually a misconfigured exporter
or network path to the collector.

**No traces appearing in Tempo/Jaeger**

Check that `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` (or the base endpoint) resolves from the
container. Confirm the `Fleans` activity source is wired: `Fleans.ServiceDefaults` registers it
via `AddSource("Fleans")`. If spans appear for `Microsoft.Orleans.*` but not `Fleans`, a
sampling rule may be dropping the `Fleans` activity source specifically.

**Logs missing `WorkflowId` in Loki**

The OTLP log exporter must be configured to capture structured key-value properties, not just
the formatted message string. In the OTel Collector, the default `otlplogs` or `loki` exporter
propagates structured attributes. Confirm your Loki pipeline uses `| json` in queries to
unpack the attributes from the OTLP body.

**All signals absent after configuration**

Restart the silo containers after adding `OTEL_EXPORTER_OTLP_ENDPOINT` — the variable is read
at startup. Verify the collector is reachable from the silo network: `docker exec fleans-api
curl -s http://otelcol:4317` should not time out (gRPC probing, a `curl` response is expected
to look like a protocol error, not a connection refused).

For additional diagnostics, see [Troubleshooting common issues](/fleans/reference/troubleshooting-common-issues/).
