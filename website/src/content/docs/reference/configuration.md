---
title: Configuration
description: Canonical reference for Fleans configuration keys and their environment-variable equivalents — covers the .NET hierarchical naming rule and the production runtime-key surface.
sidebar:
  order: 0
---

This page is the canonical lookup for every Fleans configuration key, what it does, and which environment variable equivalent operators set on a container or systemd unit. If you're trying to figure out why `Persistence__Provider` isn't taking effect on a production silo, or whether `Authentication__ClientId` belongs on the API or the Web host, this is the right page.

## The naming rule

Fleans configuration uses two notations that map onto each other via .NET's standard configuration provider:

<figure class="arch-diagram" style="--arch-ratio: 700 / 748; max-width: 740px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/config-resolution.html" src="/fleans/diagrams/config-resolution.html?embed=1" title="Configuration resolution" loading="lazy"></iframe>
  <figcaption>Every key has a colon form for appsettings.json and CLI args and a double-underscore form for environment variables; each key group switches one behaviour at silo startup. <a href="/fleans/diagrams/config-resolution.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

- **`A:B:C`** — colon-separated, used in `appsettings.json` and CLI args. Example: `Fleans:Streaming:Provider`.
- **`A__B__C`** — double-underscore-separated, used in environment variables. Example: `Fleans__Streaming__Provider`.

The mapping is mechanical: each `:` in a config key becomes `__` (double underscore) when the value is supplied as an environment variable. This is the behavior of [`EnvironmentVariablesConfigurationProvider`](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-providers#environment-variable-configuration-provider) — the same convention every ASP.NET Core / .NET host uses, not a Fleans invention.

```bash
# These three are equivalent:
appsettings.json:    "Fleans": { "Role": "Worker" }
CLI:                 dotnet run -- --Fleans:Role Worker
env var:             Fleans__Role=Worker dotnet run
```

For all keys below, the column "Env var" shows the double-underscore form; the column "Config key" shows the canonical colon form you'd put in `appsettings.json`.

## Configuration keys

These are the keys every production silo reads at startup.

### Silo role

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Role` | `Fleans:Role` | `Fleans.Api`, `Fleans.WorkerHost`, `Fleans.CustomWorkerHost` | `"Combined"` (Api), `"Worker"` (WorkerHost / CustomWorkerHost — defaulted at startup if absent) |

Allowed values: `Core`, `Worker`, `Combined` (case-insensitive), plus `Api` on `Fleans.Api`. Invalid values throw at startup. The role is stamped into the Orleans `SiloName` as `{role}-{machine}-{guid}`, visible in the Orleans Dashboard's silo membership page.

`Api` makes `Fleans.Api` an Orleans client with no grains instead of a silo: it serves only the REST API, so REST capacity scales without adding silos. It joins the cluster through the membership table like the Web and MCP hosts, so it needs the silos' `Fleans:Clustering:Provider` and connection, `Orleans:ServiceId` / `Orleans:ClusterId`, and the persistence keys (REST reads the query database). Silo-only keys (`Fleans:Reminders:*`, `Fleans:Streaming:*`, `Fleans:Kubernetes:Enabled`, `Fleans:LoadShedding:*`) are ignored. Run at least one `Core` or `Combined` silo next to it.

### Persistence

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Persistence__Provider` | `Persistence:Provider` | `Fleans.ServiceDefaults` | `"Sqlite"` (case-insensitive; accepts `Sqlite` or `Postgres`) |
| `Persistence__MaxEventsPerLoad` | `Persistence:MaxEventsPerLoad` | `Fleans.Persistence` | `1000` |
| `Persistence__SnapshotInterval` | `Persistence:SnapshotInterval` | `Fleans.Persistence` | `100` |
| `Persistence__Journal__Provider` | `Persistence:Journal:Provider` | `Fleans.ServiceDefaults` | Empty = `Postgres` (case-insensitive; accepts `Postgres`, `Citus` or `Sqlite`). On a SQLite main database without `fleans-journal` the journal stays in the SQLite file. |
| `Persistence__Journal__Citus__ShardCount` | `Persistence:Journal:Citus:ShardCount` | `Fleans.Persistence` | `128` (1–64000; validated at startup; applied when the journal tables are first distributed) |

The provider toggle. `Postgres` runs `MigrateAsync()` at startup; `Sqlite` runs `EnsureCreated()`. SQLite is dev-only and ignored in container/k8s deployments. Any other value (typo like `PostgreSQL`, empty string, whitespace) throws `ArgumentException` at silo startup so misconfigured deployments fail fast instead of silently falling back to an in-pod SQLite file.

`Persistence:Journal:Provider` picks the store for the workflow journal (event batches + snapshots), which can live in a different database from everything else — see [Separate journal database](/fleans/reference/persistence/#separate-journal-database). `Citus` is PostgreSQL with the journal tables distributed by instance id at startup. Unknown values throw `ArgumentException` at startup.

### Workflow variables

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Variables__MaxSizeBytes` | `Fleans:Variables:MaxSizeBytes` | `Fleans.Application` | `1048576` (1 MiB) |

The largest variables document, measured as JSON bytes, that one operation may pass to a workflow instance: start variables, activity completion variables (script tasks, custom tasks, REST), a user-task completion and a message delivery. The engine rejects a larger document before it applies anything. REST callers get `400 Bad Request`, and a script or custom task fails its activity. An activity failure that carries oversized variables still fails the activity, without those variables. Must be greater than `0`; the silo fails at startup otherwise. See [Variables and scope](/fleans/guides/variables-and-scope/#size-limit).

### Workflow instances in memory

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Instances__IdleCollectionAge` | `Fleans:Instances:IdleCollectionAge` | `Fleans.Application` (Core silos) | `00:02:00` |

How long a workflow instance stays activated in a silo's memory while nothing calls it. Orleans' default for every grain is 15 minutes; an instance waiting on a timer, a message or a user task would hold its activation that long (~23 KB each), so many waiting instances fill the silos' memory. A collected instance reactivates from its journal snapshot when its timer fires or its message arrives. Raise it for processes that receive a call every few minutes, to avoid the reactivation. Must be longer than one minute (Orleans' collection quantum); the silo fails at startup otherwise.

### Read-model projector

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Projector__MaxBatchSize` | `Fleans:Projector:MaxBatchSize` | `Fleans.Application` (Worker / Combined silos) | `64` (1–1000) |
| `Fleans__Projector__MaxBatchDelayMs` | `Fleans:Projector:MaxBatchDelayMs` | `Fleans.Application` (Worker / Combined silos) | `20` |
| `Fleans__Projector__MaxConcurrentBatches` | `Fleans:Projector:MaxConcurrentBatches` | `Fleans.Application` (Worker / Combined silos) | `4` |

The projector writes the read model in batches: each silo that runs it groups the projections of many instances into one database write. One batch holds up to `MaxBatchSize` projections. It waits up to `MaxBatchDelayMs` for more after its first one, so a quiet silo adds at most that much lag; under load a batch fills before the delay runs out. A silo writes up to `MaxConcurrentBatches` batches at the same time. When all of them are in flight, new projections queue (up to `MaxBatchSize` × `MaxConcurrentBatches`), and past that the projector stops taking stream notifications until the database catches up. `MaxBatchSize=1` turns batching off. Invalid values fail the silo at startup. See [Read-model batching](/fleans/reference/persistence/#read-model-batching).

### Script tasks and conditions

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Worker__EvaluatorKeyCount` | `Fleans:Worker:EvaluatorKeyCount` | `Fleans.Application` | `16` |
| `Fleans__Worker__ScriptResultTimeout` | `Fleans:Worker:ScriptResultTimeout` | `Fleans.Application` | `00:00:30` |

Conditions — gateway flows, complex-gateway activation, multi-instance completion and conditional events — are evaluated inside the workflow instance, with no network hop. Script tasks run on the script executor, a stateless worker that runs only on `Worker` / `Combined` silos: the instance dispatches the script to it and the executor reports the result back.

`EvaluatorKeyCount` is how many grain keys the engine spreads script dispatches (and conditional start events' evaluations) over. A `Core` silo sends each key to one worker silo and keeps that choice, so the key count caps how many worker silos one core silo can use. Keep it well above the number of worker silos; a larger value only adds idle activations. On `Worker` / `Combined` silos every key runs locally. Set the same value on every silo. Must be greater than `0`; the silo fails at startup otherwise.

`ScriptResultTimeout` is how long an instance waits for a dispatched script's result before it dispatches the script again — the dispatch or its result may have been lost with a silo. After 3 dispatches without a result the script task fails with a timeout, which an error boundary event can catch. A script may therefore run more than once; only its first result is applied. Keep it above the script timeout (10 s). Must be greater than `0`.

### Signals

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Signals__BucketCount` | `Fleans:Signals:BucketCount` | `Fleans.Application` | `64` |

How many buckets the subscriptions of one signal name are spread over. Each bucket is its own grain and holds the workflow instances whose id hashes to it; a broadcast (`POST /Execution/signal` or a signal throw event) calls every bucket in parallel, so no single grain handles all of a signal's subscribers. Set the same value on every Fleans process (core and worker silos, API, Web, MCP), and change it only on an empty cluster: a subscription stored under one bucket count is not found by a broadcast under another. Must be greater than `0`; the process fails at startup otherwise.

### Streaming

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Streaming__Provider` | `Fleans:Streaming:Provider` | `Fleans.ServiceDefaults` | `"redis"` (case-insensitive; accepts `redis`, `memory`, `kafka` or `azurequeue` — see [Streaming](/fleans/reference/streaming/#provider-switch)) |
| `Fleans__Streaming__Kafka__Brokers` | `Fleans:Streaming:Kafka:Brokers` | `Fleans.ServiceDefaults` (binding) | — |
| `Fleans__Streaming__Kafka__ConsumerGroup` | `Fleans:Streaming:Kafka:ConsumerGroup` | (binding) | `"fleans"` |
| `Fleans__Streaming__Kafka__TopicPrefix` | `Fleans:Streaming:Kafka:TopicPrefix` | (binding) | `"fleans-"` |
| `Fleans__Streaming__Kafka__EnableDeadLetterQueue` | `Fleans:Streaming:Kafka:EnableDeadLetterQueue` | (binding) | `false` (see [Streaming](/fleans/reference/streaming/#poison-records-and-the-dead-letter-topic)) |
| `Fleans__Streaming__Kafka__DeadLetterTopicSuffix` | `Fleans:Streaming:Kafka:DeadLetterTopicSuffix` | (binding) | `"-dlq"` |
| `Fleans__Streaming__Kafka__DeliveryRetryWindow` | `Fleans:Streaming:Kafka:DeliveryRetryWindow` | (binding) | unset — `MaxEventDeliveryTime` applies |
| `Fleans__Streaming__PollPeriod` | `Fleans:Streaming:PollPeriod` | `Fleans.ServiceDefaults` | `00:00:00.100` (Orleans default) |
| `Fleans__Streaming__MaxEventDeliveryTime` | `Fleans:Streaming:MaxEventDeliveryTime` | `Fleans.ServiceDefaults`, plugin hosts | `00:06:00` |
| `Fleans__Streaming__Redis__MaxStreamLength` | `Fleans:Streaming:Redis:MaxStreamLength` | `Fleans.ServiceDefaults` | `100000` |
| `Fleans__Streaming__Redis__TrimTimeMinutes` | `Fleans:Streaming:Redis:TrimTimeMinutes` | `Fleans.ServiceDefaults` | `5` |

`PollPeriod` is how often every pulling agent reads its queue, for any provider. A stream event (a custom task, a read-model notification) waits up to one period before delivery; a shorter period lowers that wait and polls the backend more often. Must be a positive `TimeSpan`; the silo fails at startup otherwise.

`MaxEventDeliveryTime` is how long a silo keeps retrying one stream event before it skips it for good, for any provider; set it on engine silos and plugin hosts alike. It must be longer than your slowest custom task plus the time a task can wait behind another one of the same instance — the 6-minute default covers the `rest-call` limit of 300 s. Kafka's `DeliveryRetryWindow` overrides it when set. `Redis__MaxStreamLength` caps each Redis stream; trimming ignores whether an entry was delivered, so keep it far above any backlog — see [Streaming](/fleans/reference/streaming/#delivery-window-and-redis-trimming).

`memory` is single-silo only — it silently drops cross-silo events. Use `redis`, `kafka` or `azurequeue` for any deployment with more than one silo. See [Streaming](/fleans/reference/streaming/) for at-least-once semantics.

### External tasks

Set on the core silos. See [External workers over Kafka](/fleans/guides/external-workers-kafka/).

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__ExternalTasks__Provider` | `Fleans:ExternalTasks:Provider` | `Fleans.Infrastructure` | unset — no external-task queue (accepts `kafka`, case-insensitive) |
| `Fleans__ExternalTasks__TaskTypes__0`, `__1`, … | `Fleans:ExternalTasks:TaskTypes` | `Fleans.Application` | empty — every service task goes to plugins |
| `Fleans__ExternalTasks__Kafka__Brokers` | `Fleans:ExternalTasks:Kafka:Brokers` | (binding) | — (required with `kafka`) |
| `Fleans__ExternalTasks__Kafka__TopicPrefix` | `Fleans:ExternalTasks:Kafka:TopicPrefix` | (binding) | `"fleans.tasks."` |
| `Fleans__ExternalTasks__Kafka__CreateTopics` | `Fleans:ExternalTasks:Kafka:CreateTopics` | (binding) | `true` |
| `Fleans__ExternalTasks__Kafka__NumPartitions` | `Fleans:ExternalTasks:Kafka:NumPartitions` | (binding) | `8` |
| `Fleans__ExternalTasks__Kafka__ReplicationFactor` | `Fleans:ExternalTasks:Kafka:ReplicationFactor` | (binding) | `3` |
| `Fleans__ExternalTasks__Kafka__Config__<librdkafka key>` | `Fleans:ExternalTasks:Kafka:Config:<librdkafka key>` | (binding) | — (e.g. `Config__security.protocol=SASL_SSL`; `message.timeout.ms` defaults to `10000`) |

A task type listed in `TaskTypes` goes to the queue instead of the plugin stream; listing types without a `Provider` fails at startup.

### Reminders (timers)

Every pending BPMN timer is one Orleans reminder. Every silo — engine and custom worker hosts — must use the same provider.

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Reminders__Provider` | `Fleans:Reminders:Provider` | `Fleans.ServiceDefaults` | `"Redis"` (case-insensitive; accepts `Redis` or `Postgres`) |
| `Fleans__Reminders__ReminderLoadingWindow` | `Fleans:Reminders:ReminderLoadingWindow` | `Fleans.ServiceDefaults` (bound onto Orleans `ReminderOptions`) | `00:10:00` |
| `Fleans__Reminders__RefreshReminderListPeriod` | `Fleans:Reminders:RefreshReminderListPeriod` | (binding) | `00:05:00` |

`Redis` keeps the reminders on the `orleans-redis` connection — no extra infrastructure, but every reminder of the cluster lives in one Redis key. **`Postgres` is the recommended production provider** once pending timers reach the millions; it requires `Persistence:Provider=Postgres` (the silo refuses to start otherwise) and uses `ConnectionStrings:fleans-reminders`, falling back to `ConnectionStrings:fleans`. Give the reminders their own database: every silo re-reads its slice of the reminder ring every `RefreshReminderListPeriod`, and those reads should not compete with the journal and read model. The reminders tables are created at startup; the database must exist.

`ReminderLoadingWindow` keeps only reminders due within the window in memory (it must be at least `RefreshReminderListPeriod`). A longer window rides out a longer reminder-store outage without late timers, at the cost of silo memory.

### Clustering (Orleans membership)

The membership table decides which silo is alive. Every silo **and** every Orleans client (Web, MCP, custom worker hosts) of one cluster must use the same provider — the table is also the clients' gateway list.

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Clustering__Provider` | `Fleans:Clustering:Provider` | `Fleans.ServiceDefaults` (Api, Worker, Web, MCP) | `"Redis"` (case-insensitive; accepts `Redis` or `Postgres`) |
| `Fleans__Kubernetes__Enabled` | `Fleans:Kubernetes:Enabled` | `Fleans.ServiceDefaults` (silos) | `false` (the Helm chart sets `true`) |
| `POD_NAME`, `POD_NAMESPACE`, `POD_IP` | — | Orleans Kubernetes hosting | From the Kubernetes downward API. |
| `shutdownTimeoutSeconds` | `shutdownTimeoutSeconds` | .NET host (`HostOptions.ShutdownTimeout`) | `30`; the Helm chart sets `terminationGracePeriodSeconds − 10`. |

`Redis` keeps membership on the `orleans-redis` connection. Redis replication is asynchronous, so a Redis failover can drop an acknowledged membership write. **`Postgres` is the recommended production provider**: it requires `Persistence:Provider=Postgres` (the silo refuses to start otherwise), uses `ConnectionStrings:fleans-cluster`, falling back to `ConnectionStrings:fleans`, and creates its tables at startup (the database must exist). Run that Postgres highly available (e.g. Patroni) — while the table is unreachable no silo can join or be declared dead. Unknown provider values fail startup.

`Fleans:Kubernetes:Enabled=true` turns on Orleans Kubernetes hosting: a silo whose pod is gone is declared dead immediately instead of after the probe window. The pod name becomes the silo name and **must start with the silo's role** (`core-…`, `worker-…`, `combined-…`), because Fleans placement reads the role from that prefix; the silo refuses to start otherwise. The pod's service account needs `get`, `list`, `watch` and `patch` on `pods`, and the pods need the labels `orleans/serviceId` / `orleans/clusterId` matching `Orleans__ServiceId` / `Orleans__ClusterId`. The [Helm chart](/fleans/guides/self-host-helm/#clustering-and-kubernetes-hosting) sets all of this up.

### Redis

Redis is optional. A host connects to `ConnectionStrings:orleans-redis` — and adds the Redis check to `/health` — only when a provider it uses is Redis: on a silo, any of `Fleans:Clustering:Provider`, `Fleans:Reminders:Provider` and `Fleans:Streaming:Provider` (all three default to Redis); on an Orleans client (Web, MCP, `Fleans.Api` with `Fleans:Role=Api`), the clustering provider alone. To run without Redis, set all three on every host:

```bash
Fleans__Clustering__Provider=Postgres
Fleans__Reminders__Provider=Postgres
Fleans__Streaming__Provider=Kafka        # or AzureQueue; Memory for a single silo
Persistence__Provider=Postgres           # required by the two Postgres providers
```

Without Redis the Web admin UI keeps its ASP.NET Core Data Protection keys (sign-in cookies) inside the process, so with OIDC enabled run one Web instance.

### Load shedding

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__LoadShedding__Enabled` | `Fleans:LoadShedding:Enabled` | `Fleans.ServiceDefaults` (silos) | `false` |
| `Fleans__LoadShedding__CpuThreshold` | `Fleans:LoadShedding:CpuThreshold` | `Fleans.ServiceDefaults` (silos) | `95` (Orleans default; 1–100, validated by Orleans at startup) |

Orleans load shedding: while a silo's CPU is above the threshold, its gateway rejects new requests from Orleans clients (Web, MCP, `Fleans.Api` with `Fleans:Role=Api`) instead of queueing them, and stream pulling agents slow down. The REST API answers a rejected call with `503 Service Unavailable`. Calls between silos — and REST calls a `Core` / `Combined` silo serves itself — are not shed, so pair it with `Fleans:Role=Api`. Set it on every silo.

### Admission control

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Admission__Enabled` | `Fleans:Admission:Enabled` | `Fleans.Api` | `true` |
| `Fleans__Admission__TargetLatencyMs` | `Fleans:Admission:TargetLatencyMs` | `Fleans.Api` | `500` |
| `Fleans__Admission__InitialLimit` | `Fleans:Admission:InitialLimit` | `Fleans.Api` | `64` |
| `Fleans__Admission__MinLimit` | `Fleans:Admission:MinLimit` | `Fleans.Api` | `8` |
| `Fleans__Admission__MaxLimit` | `Fleans:Admission:MaxLimit` | `Fleans.Api` | `1024` |
| `Fleans__Admission__WindowMs` | `Fleans:Admission:WindowMs` | `Fleans.Api` | `1000` |

An adaptive limit on how many REST mutations one `Fleans.Api` process has in flight: the endpoints with the `workflow-mutation` and `task-operation` policies (start, messages, signals, deploy, user-task and custom-task operations). A request over the limit is answered at once with `429 Too Many Requests` and `Retry-After: 1`; it never reaches the cluster. Reads and admin endpoints are not limited.

The limit follows the cluster. Once a second it looks at the p95 latency of the requests that finished: over `TargetLatencyMs` — or any request failed with a grain-call timeout or an Orleans error such as a shedding gateway — it is cut by a quarter, never below `MinLimit`; under the target with at least half the limit in use, it grows by one, never above `MaxLimit`. Past the CPU ceiling a cluster stops completing work rather than slowing down, and the rate it can sustain under contention is lower than the peak it reaches on an idle start, so a fixed rate limit has to sit well below the ceiling; this limit moves with it.

It is on by default because it only moves when the target is missed — a healthy cluster answers far below 500 ms — and it never limits a process below `MinLimit` concurrent requests, so a single client does not see a `429`. Set `TargetLatencyMs` to about twice the p99 of your start requests at normal load. Every `Fleans.Api` process keeps its own limit, so it scales with the number of API replicas. The fixed `RateLimiting` policies ([REST API reference](/fleans/reference/api/#rate-limiting)) still apply and run first. Limit changes are logged (EventId 8300, Warning, when it shrinks) and exported as `fleans.admission.limit` and `fleans.admission.rejected` ([Observability](/fleans/reference/observability/)). Invalid values fail at startup.

### Authentication

Auth keys are **per-host** — the API and the Web admin UI use different OIDC flows (JWT-bearer vs OIDC code-flow), so the keys split cleanly between hosts. Setting an API-only key on the Web silo (or vice-versa) has no effect.

| Env var | Config key | Read at | Applies to |
| --- | --- | --- | --- |
| `Authentication__Authority` | `Authentication:Authority` | `Fleans.Api`, `Fleans.Web`, `Fleans.Mcp` | **All HTTP hosts.** OIDC issuer URL. Setting this enables JWT enforcement on every API and MCP endpoint and OIDC sign-in on the admin UI. Auth disabled when missing. |
| `Authentication__Audience` | `Authentication:Audience` | `Fleans.Api`, `Fleans.Mcp` | JWT `aud` claim the host requires. Default: `"fleans-api"` (API), `"fleans-mcp"` (MCP). Setting on the Web silo has no effect. |
| `Authentication__ClientId` | `Authentication:ClientId` | `Fleans.Web` | **Web only.** OIDC client identifier for the Blazor Server admin UI. Setting on the API has no effect. |
| `Authentication__ClientSecret` | `Authentication:ClientSecret` | `Fleans.Web` | **Web only.** OIDC client secret. Source from a Secret/Key Vault, not appsettings, in production. |

See [Authentication](/fleans/reference/authentication/) for the full role-claim plan and reverse-proxy guidance.

### Deploy approvals and environments

See the [Deploy approvals and environments guide](/fleans/guides/deployments/).

| Env var | Config key | Read at | Default | Purpose |
| --- | --- | --- | --- | --- |
| `Fleans__Deployments__RequiredApprovals` | `Fleans:Deployments:RequiredApprovals` | `Fleans.Api`, `Fleans.Web`, `Fleans.Mcp` — **set it on all of them** | `0` | Approvals from distinct non-requesters a deploy needs. `0` deploys immediately; above it direct deploys answer 409 and go through deployment requests. |
| `Fleans__Deployments__ReviewerGroups__N` | `Fleans:Deployments:ReviewerGroups:N` | `Fleans.Api`, `Fleans.Web`, `Fleans.Mcp` | *(none)* | Groups (from `Authentication:GroupsClaim`; `roles` for roles) whose members may approve or reject. Empty: anyone but the requester. |
| `Fleans__Environment__Name` | `Fleans:Environment:Name` | `Fleans.Web` | *(empty: "this Fleans cluster")* | This cluster's name in the Admin UI. |
| `Fleans__Environment__PromotionTargets__N__Name`, `…__ApiUrl` | `Fleans:Environment:PromotionTargets:N:Name`, `…:ApiUrl` | `Fleans.Web` | *(none)* | Clusters this Admin UI can promote a version to. Names must be unique, URLs absolute — checked at startup. |
| `Fleans__Environment__PromotionTargets__N__TokenUrl`, `…__ClientId`, `…__ClientSecret`, `…__Scope` | `…:TokenUrl`, `…:ClientId`, `…:ClientSecret`, `…:Scope` | `Fleans.Web` | *(none)* | OAuth2 client credentials for a target with authentication on. |

### Connection strings

| Env var | Config key | Read at | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings__fleans` | `ConnectionStrings:fleans` | `Fleans.ServiceDefaults` | **Required when `Persistence:Provider=Postgres`.** Workflow command-side database (grain state + read model, and the journal unless `fleans-journal` is set). Throws at startup if missing. |
| `ConnectionStrings__fleans-query` | `ConnectionStrings:fleans-query` | `Fleans.ServiceDefaults` | Optional read-replica for the query-side projection. Falls back to the `fleans` write connection when unset. |
| `ConnectionStrings__fleans-journal` | `ConnectionStrings:fleans-journal` | `Fleans.ServiceDefaults` | Optional database for the workflow journal (event batches + snapshots). Falls back to the `fleans` database when unset; required when `Persistence:Journal:Provider` is not of the main provider's family (e.g. a PostgreSQL journal next to SQLite). |
| `ConnectionStrings__fleans-reminders` | `ConnectionStrings:fleans-reminders` | `Fleans.ServiceDefaults` | Optional dedicated database for the Orleans reminders when `Fleans:Reminders:Provider=Postgres` (recommended in production). Falls back to `fleans` when unset. See [Reminders](#reminders-timers). |
| `ConnectionStrings__fleans-cluster` | `ConnectionStrings:fleans-cluster` | `Fleans.ServiceDefaults` | Optional dedicated database for the Orleans membership table when `Fleans:Clustering:Provider=Postgres`. Falls back to `fleans` when unset. See [Clustering](#clustering-orleans-membership). |
| `ConnectionStrings__orleans-redis` | `ConnectionStrings:orleans-redis` | `Fleans.Api`, `Fleans.WorkerHost`, `Fleans.Web`, `Fleans.Mcp`, custom worker hosts | **Required** — Redis membership (the default `Fleans:Clustering:Provider`), Redis streams and Redis reminders. Note the name — it's `orleans-redis`, not bare `redis`. |

### .NET runtime

These are standard ASP.NET Core / .NET runtime keys, not Fleans-specific — listed here for completeness because every operator sets them.

| Env var | Effect |
| --- | --- |
| `ASPNETCORE_URLS` | Kestrel binding URL(s). Overrides `launchSettings.json` and `applicationUrl`. |
| `ASPNETCORE_HTTP_PORTS` | Newer alternative to `ASPNETCORE_URLS` for plain-port binding (e.g. `8080`). |
| `DOTNET_PRINT_TELEMETRY_MESSAGE` | Set `false` to suppress the .NET CLI telemetry banner at process startup. |
| `Orleans__ClusterId` | Orleans cluster identifier. Default `dev`; set explicitly per environment (e.g. `fleans-production`). See [Deployment / Update strategy](/fleans/reference/deployment/#update-strategy) for rolling-restart constraints. |
| `Orleans__ServiceId` | Orleans service identifier. Stable per logical service. |

## Deployment-specific cross-references

- [Deployment](/fleans/reference/deployment/) — full docker-compose / k8s / systemd examples that consume these keys end-to-end.
- [Persistence](/fleans/reference/persistence/) — provider-specific connection-string semantics and migration behavior.
- [Streaming](/fleans/reference/streaming/) — Kafka topic naming, at-least-once semantics, and the `memory`-vs-`kafka` decision.
- [Authentication](/fleans/reference/authentication/) — full IdP setup walkthrough that consumes the four `Authentication:*` keys.
