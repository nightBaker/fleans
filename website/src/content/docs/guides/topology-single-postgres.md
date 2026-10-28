---
title: "Simplest start: one PostgreSQL"
description: Run Fleans in production with nothing but PostgreSQL — one engine container, the admin UI and the plugin host, with workflow state, timers and cluster membership all in one database. No Redis, no Kafka.
---

The smallest production layout: one Fleans engine container, the admin UI, optionally the plugin
host, and **one PostgreSQL server** that holds everything — the workflow journal, the read model,
BPMN timers and the Orleans membership table. There is no Redis and no Kafka to run, back up or
monitor.

Choose it when you are starting out, when the load fits one engine container, or when your team already runs PostgreSQL and nothing
else. When you outgrow it, move to the [scalable layout](/fleans/guides/topology-scalable/): the
BPMN files, the REST API and the admin UI stay the same.

<figure class="arch-diagram">
<svg viewBox="0 0 760 424" role="img" aria-label="Simplest install: one Combined engine silo, the admin UI and an optional plugin host, all on one PostgreSQL server" xmlns="http://www.w3.org/2000/svg" font-family="inherit" style="width:100%;height:auto;max-width:760px"><defs><marker id="single-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="var(--fl-text-faint)"/></marker><marker id="single-arrow-hot" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="var(--fl-blue)"/></marker></defs><rect x="24" y="104" width="536" height="168" rx="8" fill="var(--fl-surface-2)" stroke="var(--fl-text-faint)" stroke-width="1"/><rect x="24" y="16" width="336" height="52" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="192.0" y="38" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Your backend services</text><text x="192.0" y="54" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">start, messages, signals (REST)</text><rect x="400" y="16" width="336" height="52" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="568.0" y="38" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Operators</text><text x="568.0" y="54" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">browser</text><text x="40" y="126" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="start">Orleans cluster · Memory streams</text><rect x="40" y="140" width="300" height="116" rx="8" fill="var(--fl-blue)" fill-opacity="0.12" stroke="var(--fl-blue)" stroke-width="2"/><text x="190.0" y="186" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans · Combined role</text><text x="190.0" y="202" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">one silo: REST API, workflow grains,</text><text x="190.0" y="218" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">scripts, read-model projector, timers</text><rect x="356" y="140" width="188" height="116" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="450.0" y="178" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-plugins</text><text x="450.0" y="194" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Plugin role · optional</text><text x="450.0" y="210" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">REST Caller,</text><text x="450.0" y="226" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Camunda external tasks</text><rect x="584" y="140" width="152" height="116" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="660.0" y="186" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-web</text><text x="660.0" y="202" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">admin UI</text><text x="660.0" y="218" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Orleans client</text><rect x="24" y="312" width="712" height="96" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="380.0" y="348" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">PostgreSQL · one server</text><text x="380.0" y="364" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">fleans: journal + snapshots, read model, membership, reminders</text><text x="380.0" y="380" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">camunda_external_tasks: the Camunda plugin's waiting tasks</text><path d="M300 68V140" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#single-arrow)"/><path d="M660 68V140" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#single-arrow)"/><path d="M190 256V312" fill="none" stroke="var(--fl-blue)" stroke-width="2" marker-end="url(#single-arrow-hot)"/><path d="M450 256V312" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#single-arrow)"/><path d="M660 256V312" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#single-arrow)"/><path d="M584 198H560" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#single-arrow)"/><text x="198" y="292" font-size="11.5" fill="var(--fl-text)" text-anchor="start">append by InstanceId</text></svg>
<figcaption>One engine silo in the <code>Combined</code> role does everything; the admin UI and the plugin host join the same cluster. Every byte of state lives in one PostgreSQL server.</figcaption>
</figure>

## What runs where

| Component | Image | Role |
| --- | --- | --- |
| `fleans` | `ghcr.io/nightbaker/fleans-api` | `Combined`: REST API, workflow instances, timers, messages and signals, scripts and the read-model projector — the whole engine in one silo. |
| `fleans-web` | `ghcr.io/nightbaker/fleans-web` | Admin UI, an Orleans client of the cluster. |
| `fleans-plugins` *(optional)* | `ghcr.io/nightbaker/fleans-plugins` | The [ready-made plugins](/fleans/concepts/custom-tasks/#ready-made-plugins-fleans-plugins) — REST Caller and Camunda 7 external tasks — in a `Plugin`-role silo. |
| `postgres` | `postgres:16` or a managed PostgreSQL | Everything below. |

| Data | Where | Setting |
| --- | --- | --- |
| Workflow journal, snapshots, read model, definitions | database `fleans` | `Persistence__Provider=Postgres`, `ConnectionStrings__fleans` |
| Orleans cluster membership | same database (`orleansmembershiptable`) | `Fleans__Clustering__Provider=Postgres` |
| BPMN timers (Orleans reminders) | same database (`orleansreminderstable`) | `Fleans__Reminders__Provider=Postgres` |
| Streams (plugin task dispatch, read-model notifications) | in memory of the silos | `Fleans__Streaming__Provider=Memory` |
| Camunda plugin's waiting tasks | database `camunda_external_tasks`, same server | `ConnectionStrings__camunda-external-tasks` |

Fleans creates every table at startup; you only provide an empty database and a user that owns it.

## Run it with Docker Compose

Save this as `docker-compose.yml` and set `POSTGRES_PASSWORD` in a `.env` file next to it:

```yaml
name: fleans

x-fleans-env: &fleans-env
  Persistence__Provider: Postgres
  ConnectionStrings__fleans: "Host=postgres;Database=fleans;Username=fleans;Password=${POSTGRES_PASSWORD:-fleans}"
  Fleans__Clustering__Provider: Postgres
  Fleans__Reminders__Provider: Postgres
  Fleans__Streaming__Provider: Memory
  Orleans__ServiceId: fleans
  Orleans__ClusterId: fleans

x-http-health: &http-health
  # The aspnet base image has no curl: a plain HTTP GET /health over bash's /dev/tcp.
  test: ["CMD-SHELL", "bash -c 'exec 3<>/dev/tcp/localhost/8080 && printf \"GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n\" >&3 && read r <&3 && [[ \"$$r\" == *200* ]]'"]
  interval: 10s
  timeout: 5s
  retries: 15
  start_period: 30s

services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: fleans
      POSTGRES_USER: fleans
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-fleans}
    volumes: [pgdata:/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U fleans -d fleans"]
      interval: 5s
      retries: 20

  fleans:
    image: ${FLEANS_API_IMAGE:-ghcr.io/nightbaker/fleans-api:0.10.0}
    environment:
      <<: *fleans-env
      Fleans__Role: Combined
    ports: ["8080:8080"]
    healthcheck: *http-health
    depends_on:
      postgres: { condition: service_healthy }
    restart: unless-stopped

  fleans-web:
    image: ${FLEANS_WEB_IMAGE:-ghcr.io/nightbaker/fleans-web:0.10.0}
    environment: *fleans-env
    ports: ["8081:8080"]
    depends_on:
      fleans: { condition: service_healthy }
    restart: unless-stopped

  fleans-plugins:
    image: ${FLEANS_PLUGINS_IMAGE:-ghcr.io/nightbaker/fleans-plugins:0.10.0}
    environment:
      <<: *fleans-env
      # The Camunda external-task plugin's table, in its own database on the same server.
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=fleans;Password=${POSTGRES_PASSWORD:-fleans}"
    ports: ["8082:8080"]
    depends_on:
      fleans: { condition: service_healthy }
    restart: unless-stopped

volumes:
  pgdata:
```

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -hex 16)" > .env
docker compose up -d --wait
```

The REST API is on <http://localhost:8080> (Swagger at `/swagger`), the admin UI on
<http://localhost:8081>, and the plugin host serves the Camunda `/engine-rest` API on
<http://localhost:8082>. Drop the `fleans-plugins` service if you don't use the plugins.

To use a PostgreSQL server you already have, delete the `postgres` service and point
`ConnectionStrings__fleans` (and the Camunda plugin's connection string) at it.

## Run it on Kubernetes

The same layout with the [Helm chart](/fleans/guides/self-host-helm/):

```yaml
# values-single-postgres.yaml
# One engine silo, the admin UI and MCP, the plugin host — and one PostgreSQL. No Redis.
persistence:
  provider: Postgres
postgres:
  enabled: true          # or false + ConnectionStrings__fleans in extraEnv for your own server
clustering:
  provider: Postgres
reminders:
  provider: Postgres
streaming:
  provider: Memory
redis:
  enabled: false
core:
  replicas: 1            # Memory streams: keep one engine silo
plugins:
  enabled: true          # optional: REST Caller + Camunda external tasks
```

```bash
gh release download v0.10.0 --repo nightBaker/fleans -p 'fleans-0.10.0.tgz'
helm install fleans ./fleans-0.10.0.tgz -f values-single-postgres.yaml
```

The chart's engine silo runs in the `Core` role; with no `Worker` silos it runs scripts and the
read-model projector itself, exactly like `Combined`.

## Limits of this layout

- **One engine silo.** Memory streams live inside the silos, so keep a single `fleans`
  (`core.replicas: 1`). A restart is a short outage of the REST API; nothing durable is lost —
  instances, timers and messages are in PostgreSQL, and timers that came due during the restart
  fire when the silo is back.
- **In-flight stream messages die with the process.** If the engine crashes in the instant a
  plugin task is being handed to the plugin host, that task stays *active* and needs a manual
  retry; read-model notifications repair themselves on the instance's next activation. Start the
  plugin host before you run processes that use its tasks.
- **One database to size.** Every step is a write to the same PostgreSQL. When its CPU or I/O
  becomes the limit, that is the signal to move on.

## When to move on

Move to the [scalable layout](/fleans/guides/topology-scalable/) when you need more than one
engine silo (availability or CPU), when PostgreSQL write load becomes the bottleneck, or when you
want the admin UI's queries off the database that runs workflows. The move is configuration only,
but until 1.0 there is **no data migration between layouts or releases**: start it on fresh
databases.

## See also

- [Recommended for scale](/fleans/guides/topology-scalable/)
- [Self-host with Helm](/fleans/guides/self-host-helm/) · [Docker Compose bundle](/fleans/guides/self-host-docker-compose/)
- [Persistence](/fleans/reference/persistence/) · [Streaming](/fleans/reference/streaming/) · [Configuration](/fleans/reference/configuration/)
