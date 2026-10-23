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

Allowed values: `Core`, `Worker`, `Combined` (case-insensitive). Invalid values throw at startup. The role is stamped into the Orleans `SiloName` as `{role}-{machine}-{guid}`, visible in the Orleans Dashboard's silo membership page.

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

### Script and condition evaluators

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Worker__EvaluatorKeyCount` | `Fleans:Worker:EvaluatorKeyCount` | `Fleans.Application` | `16` |

How many grain keys the engine spreads script and condition evaluations over. The evaluators are stateless workers that run only on `Worker` / `Combined` silos. A `Core` silo sends each key to one worker silo and keeps that choice, so the key count caps how many worker silos one core silo can use. Keep it well above the number of worker silos; a larger value only adds idle activations. On `Worker` / `Combined` silos every key runs locally. Set the same value on every silo. Must be greater than `0`; the silo fails at startup otherwise.

### Streaming

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Streaming__Provider` | `Fleans:Streaming:Provider` | `Fleans.ServiceDefaults` | `"memory"` (case-insensitive; accepts `memory`, `kafka` or `azurequeue` — see [Streaming](/fleans/reference/streaming/#provider-switch)) |
| `Fleans__Streaming__Kafka__Brokers` | `Fleans:Streaming:Kafka:Brokers` | `Fleans.ServiceDefaults` (binding) | — |
| `Fleans__Streaming__Kafka__ConsumerGroup` | `Fleans:Streaming:Kafka:ConsumerGroup` | (binding) | `"fleans"` |
| `Fleans__Streaming__Kafka__TopicPrefix` | `Fleans:Streaming:Kafka:TopicPrefix` | (binding) | `"fleans-"` |
| `Fleans__Streaming__Kafka__EnableDeadLetterQueue` | `Fleans:Streaming:Kafka:EnableDeadLetterQueue` | (binding) | `false` (see [Streaming](/fleans/reference/streaming/#poison-records-and-the-dead-letter-topic)) |
| `Fleans__Streaming__Kafka__DeadLetterTopicSuffix` | `Fleans:Streaming:Kafka:DeadLetterTopicSuffix` | (binding) | `"-dlq"` |
| `Fleans__Streaming__Kafka__DeliveryRetryWindow` | `Fleans:Streaming:Kafka:DeliveryRetryWindow` | (binding) | `00:01:00` (Orleans default) |

`memory` is single-silo only — it silently drops cross-silo events. Use `kafka` or `azurequeue` for any deployment with more than one silo. See [Streaming](/fleans/reference/streaming/) for at-least-once semantics.

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

### Authentication

Auth keys are **per-host** — the API and the Web admin UI use different OIDC flows (JWT-bearer vs OIDC code-flow), so the keys split cleanly between hosts. Setting an API-only key on the Web silo (or vice-versa) has no effect.

| Env var | Config key | Read at | Applies to |
| --- | --- | --- | --- |
| `Authentication__Authority` | `Authentication:Authority` | `Fleans.Api`, `Fleans.Web` | **Both hosts.** OIDC issuer URL. Setting this enables JWT enforcement on every API endpoint (API) and OIDC sign-in on the admin UI. Auth disabled when missing. |
| `Authentication__Audience` | `Authentication:Audience` | `Fleans.Api` | **API only.** JWT `aud` claim the API requires. Default: `"fleans-api"`. Setting on the Web silo has no effect. |
| `Authentication__ClientId` | `Authentication:ClientId` | `Fleans.Web` | **Web only.** OIDC client identifier for the Blazor Server admin UI. Setting on the API has no effect. |
| `Authentication__ClientSecret` | `Authentication:ClientSecret` | `Fleans.Web` | **Web only.** OIDC client secret. Source from a Secret/Key Vault, not appsettings, in production. |

See [Authentication](/fleans/reference/authentication/) for the full role-claim plan and reverse-proxy guidance.

### Deploy approvals and environments

See the [Deploy approvals and environments guide](/fleans/guides/deployments/).

| Env var | Config key | Read at | Default | Purpose |
| --- | --- | --- | --- | --- |
| `Fleans__Deployments__RequiredApprovals` | `Fleans:Deployments:RequiredApprovals` | `Fleans.Api`, `Fleans.Web`, `Fleans.Mcp` — **set it on all of them** | `0` | Approvals from distinct non-requesters a deploy needs. `0` deploys immediately; above it direct deploys answer 409 and go through deployment requests. |
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
| `ConnectionStrings__orleans-redis` | `ConnectionStrings:orleans-redis` | `Fleans.Api`, `Fleans.WorkerHost`, `Fleans.Web`, `Fleans.Mcp`, custom worker hosts | **Required** — Redis membership (the default `Fleans:Clustering:Provider`), `PubSubStore`, Redis streams and Redis reminders. Note the name — it's `orleans-redis`, not bare `redis`. |

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
