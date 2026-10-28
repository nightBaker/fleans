---
title: Recommended for scale
description: The layout Fleans 1.0 is built around — REST, workflow, script and plugin silos scaled apart, the journal sharded on Citus, the read model on a primary with replicas, and streams on Kafka.
---

This is the layout of the [Road to 1.0.0](/fleans/roadmap/v1/#target-architecture), and every
piece of it is available in 0.10.0. Each kind of load gets its own deployment and scales on its own
signal, and no single database takes every write:

- the **journal** — the only source of truth — is sharded by `InstanceId` on Citus, so writes grow
  with storage nodes the way compute grows with silos;
- the **read model** is built off the hot path by the projector on the Worker silos, and the admin
  UI, MCP and REST lists read a replica;
- **membership and reminders** live in a small database of their own;
- **streams** run on Kafka. Nothing uses Redis.

Start with the [single-PostgreSQL layout](/fleans/guides/topology-single-postgres/) unless you
already know you need this one.

<figure class="arch-diagram">
<svg viewBox="0 0 760 560" role="img" aria-label="Scalable install: REST clients, Core, Worker and plugin silos scaled apart; journal on Citus, read model on a primary with a replica, membership and reminders on their own database, streams on Kafka" xmlns="http://www.w3.org/2000/svg" font-family="inherit" style="width:100%;height:auto;max-width:760px"><defs><marker id="scale-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="var(--fl-text-faint)"/></marker><marker id="scale-arrow-hot" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="var(--fl-blue)"/></marker></defs><rect x="24" y="196" width="712" height="156" rx="8" fill="var(--fl-surface-2)" stroke="var(--fl-text-faint)" stroke-width="1"/><rect x="24" y="16" width="336" height="52" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="192.0" y="38" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Your backend services</text><text x="192.0" y="54" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">start, messages, signals (REST)</text><rect x="400" y="16" width="336" height="52" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="568.0" y="38" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Operators</text><text x="568.0" y="54" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">browser, AI agents</text><rect x="24" y="100" width="336" height="56" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="192.0" y="124" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-api × N · Api role</text><text x="192.0" y="140" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">REST only · admission limit answers 429</text><rect x="400" y="100" width="336" height="56" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="568.0" y="124" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-web, fleans-mcp</text><text x="568.0" y="140" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">admin UI, MCP · Orleans clients</text><text x="296" y="218" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="start">Orleans cluster · Kafka streams, implicit subscriptions</text><rect x="40" y="232" width="240" height="104" rx="8" fill="var(--fl-blue)" fill-opacity="0.12" stroke="var(--fl-blue)" stroke-width="2"/><text x="160.0" y="264" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-core × N</text><text x="160.0" y="280" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">Core role · workflow grains,</text><text x="160.0" y="296" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">messages, signals, timers,</text><text x="160.0" y="312" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">conditions in process</text><rect x="296" y="232" width="240" height="104" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="416.0" y="264" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-worker × M</text><text x="416.0" y="280" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Worker role · scripts,</text><text x="416.0" y="296" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">read-model projector</text><text x="416.0" y="312" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">(batched upserts)</text><rect x="552" y="232" width="168" height="104" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="636.0" y="264" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">fleans-plugins × K</text><text x="636.0" y="280" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Plugin role ·</text><text x="636.0" y="296" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">REST Caller,</text><text x="636.0" y="312" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">Camunda external tasks</text><rect x="24" y="400" width="166" height="96" rx="8" fill="var(--fl-blue)" fill-opacity="0.12" stroke="var(--fl-blue)" stroke-width="2"/><text x="107.0" y="436" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Citus</text><text x="107.0" y="452" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">journal + snapshots</text><text x="107.0" y="468" font-size="11.5" fill="var(--fl-text)" text-anchor="middle">sharded by InstanceId</text><rect x="206" y="400" width="166" height="96" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="289.0" y="428" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">PostgreSQL</text><text x="289.0" y="444" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">read model, grain state</text><text x="289.0" y="460" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">primary → replica</text><text x="289.0" y="476" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">(reads)</text><rect x="388" y="400" width="166" height="96" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="471.0" y="436" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">PostgreSQL ops</text><text x="471.0" y="452" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">membership,</text><text x="471.0" y="468" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">reminders</text><rect x="570" y="400" width="166" height="96" rx="8" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25"/><text x="653.0" y="428" font-size="13" font-weight="600" fill="var(--fl-text)" text-anchor="middle">Kafka</text><text x="653.0" y="444" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">task dispatch,</text><text x="653.0" y="460" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">projection</text><text x="653.0" y="476" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="middle">notifications</text><path d="M192 68V100" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><path d="M568 68V100" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><path d="M192 156V232" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><text x="200" y="184" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="start">grain calls</text><path d="M568 156V196" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><path d="M107 336V400" fill="none" stroke="var(--fl-blue)" stroke-width="2" marker-end="url(#scale-arrow-hot)"/><text x="116" y="380" font-size="11.5" fill="var(--fl-text)" text-anchor="start">append</text><path d="M330 336V400" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><text x="338" y="380" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="start">upserts</text><path d="M471 352V400" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-end="url(#scale-arrow)"/><path d="M653 352V400" fill="none" stroke="var(--fl-text-faint)" stroke-width="1.25" marker-start="url(#scale-arrow)" marker-end="url(#scale-arrow)"/><text x="24" y="528" font-size="11.5" fill="var(--fl-text-muted)" text-anchor="start">Hot path: one append per step to the instance's Citus shard. Projection, plugins and the admin UI's lists run off it.</text></svg>
<figcaption>The highlighted path is the only synchronous write: a step appends to its instance's Citus shard. Projection, plugins and reads run on other pods and other databases.</figcaption>
</figure>

## Deployments

| Deployment | Image · role | Hosts | Scale on |
| --- | --- | --- | --- |
| `fleans-api` | `fleans-api` · `Api` | REST only — an Orleans client with no grains; the [admission limit](/fleans/reference/configuration/#admission-control) answers `429` when the cluster is saturated | requests per second, CPU |
| `fleans-core` | `fleans-api` · `Core` | workflow instances, message and signal correlation, timers; conditions run in process | CPU, `fleans.journal.append.duration` |
| `fleans-worker` | `fleans-worker` · `Worker` | script tasks (stateless workers) and the read-model projector | CPU, `fleans.projection.lag` |
| `fleans-plugins` | `fleans-plugins` · `Plugin` | custom-task plugins — REST Caller, Camunda external tasks, or your own host image | CPU, `fleans.kafka.consumer.lag` |
| `fleans-web`, `fleans-mcp` | `fleans-web`, `fleans-mcp` | admin UI, MCP server; read the replica | manual |

## Data stores

| Data | Store | Settings |
| --- | --- | --- |
| Journal + snapshots | **Citus** — coordinator + workers; tables distributed by `InstanceId` at startup (128 shards, `Persistence__Journal__Citus__ShardCount`) | `Persistence__Journal__Provider=Citus`, `ConnectionStrings__fleans-journal` |
| Read model, definitions, other grain state | **PostgreSQL primary** | `Persistence__Provider=Postgres`, `ConnectionStrings__fleans` |
| Every list and read-model query | **its streaming replica** | `ConnectionStrings__fleans-query` |
| Membership, reminders (BPMN timers) | **PostgreSQL "ops"** — small, but atomic writes and HA matter | `Fleans__Clustering__Provider=Postgres`, `ConnectionStrings__fleans-cluster`, `Fleans__Reminders__Provider=Postgres`, `ConnectionStrings__fleans-reminders` |
| Streams | **Kafka** | `Fleans__Streaming__Provider=Kafka`, `Fleans__Streaming__Kafka__Brokers` |

Every silo and client, the plugin host included, gets the same settings: a silo on a different
membership table or stream provider would form a cluster of its own. Fleans creates all tables at
startup — including distributing the journal tables over the Citus workers — so each store only
needs an empty database. Core silos create the schemas, so start them before the others.

For high availability run each PostgreSQL with Patroni or a managed service, and Citus with a
Patroni pair per node (open-source Citus has no automatic failover). The Kafka provider connects to
**plaintext brokers only** — run Kafka inside the cluster network (for example with Strimzi), not a
managed SASL/TLS service; see [Streaming](/fleans/reference/streaming/#production-readiness-gaps).

## Kubernetes

Provision Citus, the two PostgreSQL servers and Kafka first, put their connection strings in
Secrets, then install the [Helm chart](/fleans/guides/self-host-helm/) with:

```yaml
# values-scalable.yaml
# REST, workflow grains, scripts and plugins on their own pods; the journal on Citus, the read
# model on a primary with a read replica, membership and reminders on their own database, and
# streams on Kafka. No Redis. Create the Secrets below first.
api:
  enabled: true
  replicas: 2
core:
  replicas: 3
  autoscaling:
    enabled: true
    minReplicas: 3
    maxReplicas: 12
worker:
  enabled: true
  replicas: 2
  autoscaling:
    enabled: true
    minReplicas: 2
    maxReplicas: 8
plugins:
  enabled: true
  env:
    - name: ConnectionStrings__camunda-external-tasks
      valueFrom: { secretKeyRef: { name: fleans-camunda-db, key: connection-string } }
persistence:
  provider: Postgres
postgres:
  enabled: false
clustering:
  provider: Postgres
  postgres:
    existingSecret: fleans-ops-db
reminders:
  provider: Postgres
  postgres:
    existingSecret: fleans-ops-db
streaming:
  provider: Kafka
  kafka:
    brokers: fleans-kafka-bootstrap.kafka.svc:9092
redis:
  enabled: false
extraEnv:
  # Read model and other grain state — the primary; reads — the replica.
  - name: ConnectionStrings__fleans
    valueFrom: { secretKeyRef: { name: fleans-db, key: primary } }
  - name: ConnectionStrings__fleans-query
    valueFrom: { secretKeyRef: { name: fleans-db, key: replica } }
  # Journal and snapshots — Citus, sharded by InstanceId.
  - name: Persistence__Journal__Provider
    value: Citus
  - name: ConnectionStrings__fleans-journal
    valueFrom: { secretKeyRef: { name: fleans-journal-db, key: connection-string } }
```

```bash
kubectl create secret generic fleans-db \
  --from-literal=primary='Host=pg-primary;Database=fleans;Username=fleans;Password=...' \
  --from-literal=replica='Host=pg-replica;Database=fleans;Username=fleans;Password=...'
kubectl create secret generic fleans-journal-db \
  --from-literal=connection-string='Host=citus-coordinator;Database=journal;Username=fleans;Password=...'
kubectl create secret generic fleans-ops-db \
  --from-literal=connection-string='Host=pg-ops;Database=ops;Username=fleans;Password=...'
kubectl create secret generic fleans-camunda-db \
  --from-literal=connection-string='Host=pg-primary;Database=camunda_external_tasks;Username=fleans;Password=...'

gh release download v0.10.0 --repo nightBaker/fleans -p 'fleans-0.10.0.tgz'
helm install fleans ./fleans-0.10.0.tgz -f values-scalable.yaml
```

The chart turns on Orleans Kubernetes hosting, so a deleted pod leaves the cluster at once. For
autoscaling on the Fleans metrics (append latency, projection lag, Kafka consumer lag) instead of
CPU, see [Backpressure and autoscaling](/fleans/guides/self-host-helm/#backpressure-and-autoscaling).

## Try it on one machine

The same layout in Docker Compose — two Core silos, one Worker, the plugin host, Citus with two
workers, a read-model primary with a streaming replica, the ops database and a single Kafka
broker. It is for trying the topology out, not for production (no HA, trust auth between the
Citus nodes).

<details>
<summary><code>docker-compose.yml</code></summary>

```yaml
# Fleans — recommended layout for scale, on one machine for a trial.
name: fleans-scalable

x-pg: &pg
  POSTGRES_USER: postgres
  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-fleans}

x-fleans-env: &fleans-env
  Orleans__ServiceId: fleans
  Orleans__ClusterId: fleans
  Persistence__Provider: Postgres
  # Read model and the other grain state: written on the primary, read from the replica.
  ConnectionStrings__fleans: "Host=postgres;Database=fleans;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
  ConnectionStrings__fleans-query: "Host=postgres-replica;Database=fleans;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
  # Journal and snapshots: Citus, sharded by InstanceId.
  Persistence__Journal__Provider: Citus
  ConnectionStrings__fleans-journal: "Host=citus-coord;Database=journal;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
  # Membership and reminders: their own small database.
  Fleans__Clustering__Provider: Postgres
  ConnectionStrings__fleans-cluster: "Host=postgres-ops;Database=ops;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
  Fleans__Reminders__Provider: Postgres
  ConnectionStrings__fleans-reminders: "Host=postgres-ops;Database=ops;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
  # Streams: Kafka.
  Fleans__Streaming__Provider: Kafka
  Fleans__Streaming__Kafka__Brokers: "kafka:9092"

x-pg-health: &pg-health
  test: ["CMD-SHELL", "pg_isready -U postgres"]
  interval: 5s
  retries: 30

x-citus: &citus
  image: ${CITUS_IMAGE:-citusdata/citus:12.1}
  environment:
    <<: *pg
    POSTGRES_DB: journal
    # Trial only: the coordinator connects to the workers without a password.
    POSTGRES_HOST_AUTH_METHOD: trust
  healthcheck: *pg-health

x-http-health: &http-health
  # The aspnet base image has no curl: a plain HTTP GET /health over bash's /dev/tcp.
  test: ["CMD-SHELL", "bash -c 'exec 3<>/dev/tcp/localhost/8080 && printf \"GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n\" >&3 && read r <&3 && [[ \"$$r\" == *200* ]]'"]
  interval: 10s
  timeout: 5s
  retries: 15
  start_period: 30s

configs:
  replication-hba:
    content: |
      echo "host replication all all scram-sha-256" >> "$$PGDATA/pg_hba.conf"

services:
  # ── Journal: Citus coordinator + 2 workers ──────────────────────────────
  citus-coord: *citus
  citus-w1: *citus
  citus-w2: *citus
  citus-setup:
    image: ${CITUS_IMAGE:-citusdata/citus:12.1}
    environment: { PGPASSWORD: "${POSTGRES_PASSWORD:-fleans}" }
    entrypoint: ["bash", "-c"]
    command:
      - >-
        set -e;
        psql -h citus-coord -U postgres -d journal -c "SELECT citus_set_coordinator_host('citus-coord', 5432)";
        for w in citus-w1 citus-w2; do
          psql -h citus-coord -U postgres -d journal -c "SELECT * FROM citus_add_node('$$w', 5432)";
        done
    restart: "no"
    depends_on:
      citus-coord: { condition: service_healthy }
      citus-w1: { condition: service_healthy }
      citus-w2: { condition: service_healthy }

  # ── Read model: primary + streaming replica ─────────────────────────────
  postgres:
    image: postgres:16-alpine
    environment: { <<: *pg, POSTGRES_DB: fleans }
    configs:
      - source: replication-hba
        target: /docker-entrypoint-initdb.d/replication-hba.sh
    healthcheck: *pg-health
  postgres-replica:
    image: postgres:16-alpine
    user: postgres
    environment: { PGPASSWORD: "${POSTGRES_PASSWORD:-fleans}" }
    entrypoint: ["bash", "-c"]
    command:
      - >-
        set -e;
        if [ ! -s "$$PGDATA/PG_VERSION" ]; then
          until pg_basebackup -h postgres -U postgres -D "$$PGDATA" -R -X stream; do sleep 1; done;
          chmod 0700 "$$PGDATA";
        fi;
        exec postgres -c hot_standby=on -c hot_standby_feedback=on
    healthcheck: *pg-health
    depends_on:
      postgres: { condition: service_healthy }

  # ── Membership + reminders ──────────────────────────────────────────────
  postgres-ops:
    image: postgres:16-alpine
    environment: { <<: *pg, POSTGRES_DB: ops }
    healthcheck: *pg-health

  # ── Streams ─────────────────────────────────────────────────────────────
  kafka:
    image: apache/kafka:3.9.1
    environment:
      KAFKA_NODE_ID: 1
      KAFKA_PROCESS_ROLES: broker,controller
      KAFKA_LISTENERS: PLAINTEXT://:9092,CONTROLLER://:9093
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://kafka:9092
      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
      KAFKA_CONTROLLER_QUORUM_VOTERS: 1@kafka:9093
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: 1
      KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: 0
    healthcheck:
      test: ["CMD-SHELL", "/opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server localhost:9092 >/dev/null 2>&1"]
      interval: 5s
      retries: 30

  # ── Orleans cluster ─────────────────────────────────────────────────────
  fleans-core:
    image: ${FLEANS_API_IMAGE:-ghcr.io/nightbaker/fleans-api:0.10.0}
    environment: { <<: *fleans-env, Fleans__Role: Core }
    deploy: { replicas: 2 }
    restart: on-failure
    healthcheck: *http-health
    depends_on:
      citus-setup: { condition: service_completed_successfully }
      postgres-replica: { condition: service_healthy }
      postgres-ops: { condition: service_healthy }
      kafka: { condition: service_healthy }

  fleans-worker:
    image: ${FLEANS_WORKER_IMAGE:-ghcr.io/nightbaker/fleans-worker:0.10.0}
    environment: { <<: *fleans-env, Fleans__Role: Worker }
    deploy: { replicas: 1 }
    restart: on-failure
    depends_on:
      fleans-core: { condition: service_healthy }

  fleans-plugins:
    image: ${FLEANS_PLUGINS_IMAGE:-ghcr.io/nightbaker/fleans-plugins:0.10.0}
    environment:
      <<: *fleans-env
      ConnectionStrings__camunda-external-tasks: "Host=postgres;Database=camunda_external_tasks;Username=postgres;Password=${POSTGRES_PASSWORD:-fleans}"
    restart: on-failure
    depends_on:
      fleans-core: { condition: service_healthy }

  # ── Clients: REST, admin UI ─────────────────────────────────────────────
  fleans-api:
    image: ${FLEANS_API_IMAGE:-ghcr.io/nightbaker/fleans-api:0.10.0}
    environment: { <<: *fleans-env, Fleans__Role: Api }
    ports: ["8080:8080"]
    restart: on-failure
    depends_on:
      fleans-core: { condition: service_healthy }

  fleans-web:
    image: ${FLEANS_WEB_IMAGE:-ghcr.io/nightbaker/fleans-web:0.10.0}
    environment: *fleans-env
    ports: ["8081:8080"]
    restart: on-failure
    depends_on:
      fleans-core: { condition: service_healthy }
```

</details>

```bash
docker compose up -d --wait
```

`citusdata/citus` is published for `amd64` only; on an ARM machine it runs under emulation and is
slow. The REST API is on <http://localhost:8080>, the admin UI on <http://localhost:8081>. Scale a
role with `docker compose up -d --scale fleans-core=3`.

## How it performs

On one laptop (10 CPUs) this layout sustained 150–200 instances per second of a 10-script process
at p99 under 1 s, and adding Core silos or Citus workers scaled throughput with an efficiency of
about 0.87. The numbers, the method and the 1.0 targets are on the
[roadmap](/fleans/roadmap/v1/#how-it-is-measured) and [Load testing](/fleans/reference/load-testing/).

## See also

- [Simplest start: one PostgreSQL](/fleans/guides/topology-single-postgres/)
- [Road to 1.0.0](/fleans/roadmap/v1/) — why each store is where it is
- [Self-host with Helm](/fleans/guides/self-host-helm/) · [Persistence](/fleans/reference/persistence/) · [Streaming](/fleans/reference/streaming/) · [Observability](/fleans/reference/observability/)
