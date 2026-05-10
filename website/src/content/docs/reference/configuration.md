---
title: Configuration
description: Canonical reference for Fleans configuration keys and their environment-variable equivalents — covers the .NET hierarchical naming rule and the two-tier model (Aspire dev knobs vs runtime config keys).
sidebar:
  order: 0
---

{/* drift-guard:
  - Fleans.Api/Program.cs:28,36,52,65 (Authentication:Authority, Authentication:Audience, Fleans:Role, ConnectionStrings:orleans-redis)
  - Fleans.Web/Program.cs:55,56,82 (Authentication:Authority, ClientId, ClientSecret)
  - Fleans.WorkerHost/Program.cs:22,24,27,36 (Fleans:Role, ConnectionStrings:orleans-redis)
  - Fleans.CustomWorkerHost/Program.cs:19,21,24,33 (Fleans:Role, ConnectionStrings:orleans-redis)
  - Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:23,29,32,38,39 (Persistence:Provider, ConnectionStrings:fleans, ConnectionStrings:fleans-query, FLEANS_SQLITE_CONNECTION, FLEANS_QUERY_CONNECTION)
  - Fleans.ServiceDefaults/FleanStreamingExtensions.cs:18,25 (Fleans:Streaming:Provider, Fleans:Streaming:Kafka section binding)
  - Fleans.Streaming.Kafka/KafkaStreamingOptions.cs:8 (Brokers property)
  - Fleans.Aspire/Program.cs:10,15,76,77,95,96,106,109,122-124,151,166,174 (Aspire-only knobs + WithEnvironment forwarding)
  pinned at branch=docs/403-config-reference SHA=329b0f3; refresh if any of the above change */}

This page is the canonical lookup for every Fleans configuration key, what it does, where the value is read from in source, and which environment variable equivalent operators set on a container or systemd unit. If you're trying to figure out why setting `FLEANS_PERSISTENCE_PROVIDER` on a production silo doesn't work, or whether `Authentication__ClientId` belongs on the API or the Web host, this is the right page.

## The naming rule

Fleans configuration uses two notations that map onto each other via .NET's standard configuration provider:

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

## Two tiers, two naming conventions

Fleans has **two distinct families of configuration values**, and they look different on purpose:

| Tier | Naming | Who reads them | Purpose |
| --- | --- | --- | --- |
| **Tier 1** — *Aspire / SQLite-mode dev knobs* | `FLEANS_X` (uppercase, single underscore) | `Fleans.Aspire/Program.cs` (provisioning) + SQLite branch of `FleansPersistenceExtensions.cs` (runtime) | Convenience switches for `dotnet run --project Fleans.Aspire` and SQLite-mode dev silos. |
| **Tier 2** — *Runtime configuration keys* | `Fleans__X` / `ConnectionStrings__X` / `Authentication__X` / `Persistence__X` (PascalCase, double underscore) | Every silo's `Program.cs` directly via `builder.Configuration["A:B"]` | The canonical production form. Used by docker-compose, k8s, and systemd deployments. |

If you're deploying to production, you'll set Tier 2 keys. If you're running `dotnet run --project Fleans.Aspire` for local development, Tier 1 knobs are the convenience layer that drive Aspire's container-provisioning step (which then forwards Tier 2 env vars to the silos via `WithEnvironment`).

## Tier 1 — Aspire / SQLite-mode dev knobs

These keys are read with the literal `FLEANS_X` form; they don't map onto any `appsettings.json` hierarchy. The first two control what Aspire provisions; the next two are the SQLite connection strings that `Fleans.ServiceDefaults` reads at runtime when SQLite is the active provider; `FLEANS_ROLE` overrides the role label Aspire stamps on `fleans-core`; the last is a test-only flag.

| Env var | Read at | Effect |
| --- | --- | --- |
| `FLEANS_PERSISTENCE_PROVIDER` | `Fleans.Aspire/Program.cs:10` | `Sqlite` (default) or `Postgres` — controls whether Aspire provisions a Postgres container and forwards `Persistence__Provider=Postgres` to the silos. Read **only** by the Aspire AppHost; setting it on a self-hosted silo does nothing — use `Persistence__Provider` instead. |
| `FLEANS_STREAMING_PROVIDER` | `Fleans.Aspire/Program.cs:15` | `Memory` (default) or `Kafka` — controls Kafka container provisioning. Read **only** by the Aspire AppHost; the silo-side equivalent is `Fleans__Streaming__Provider`. |
| `FLEANS_SQLITE_CONNECTION` | `Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:38` | SQLite write-side connection string. Read by every silo when `Persistence:Provider=Sqlite`. Defaults to `DataSource=fleans-dev.db`. |
| `FLEANS_QUERY_CONNECTION` | `Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:39` | SQLite read-replica connection string. Optional; falls back to the write connection when unset. |
| `FLEANS_ROLE` | `Fleans.Aspire/Program.cs:106` | Overrides the Aspire-default `Fleans__Role` injection on `fleans-core`. Default: `Combined` in dev mode (`dotnet run --project Fleans.Aspire`), `Core` in publish mode (`aspire publish ...`) — the publish-mode default matches the Helm chart's `deployment-core.yaml`. Useful for testing the Core/Worker placement split locally without editing source. |
| `FLEANS_PG_TESTS=1` | EF / Testcontainers tests | When set, `dotnet test` runs the parametrised `[DataRow(PersistenceProvider.Postgres)]` rows against a Testcontainers-managed Postgres image. Without it, those rows surface as `Inconclusive`. |

:::note
The `FLEANS_X` naming is historical. Two of the five (`FLEANS_PERSISTENCE_PROVIDER`, `FLEANS_STREAMING_PROVIDER`) are strictly Aspire opt-ins. The two SQLite connection strings are read at runtime by silo code in the SQLite branch — but production deployments use Postgres + `ConnectionStrings__fleans` instead, so they don't typically appear there.
:::

## Tier 2 — Runtime configuration keys

These are the keys every production silo reads at startup. The "Read at" column points to the exact line in source where the value is consumed; if that line moves or the key is renamed, the drift-guard at the top of this page flags the doc as stale.

### Silo role

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Role` | `Fleans:Role` | `Fleans.Api/Program.cs:52`, `Fleans.WorkerHost/Program.cs:27`, `Fleans.CustomWorkerHost/Program.cs:24` | `"Combined"` (Api), `"Worker"` (WorkerHost / CustomWorkerHost — defaulted at startup if absent) |

Allowed values: `Core`, `Worker`, `Combined` (case-insensitive). Invalid values throw at startup. The role is stamped into the Orleans `SiloName` as `{role}-{machine}-{guid}`, visible in the Orleans Dashboard's silo membership page.

### Persistence

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Persistence__Provider` | `Persistence:Provider` | `Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:23` | `"Sqlite"` (case-insensitive; accepts `Sqlite` or `Postgres`) |

The provider toggle. `Postgres` runs `MigrateAsync()` at startup; `Sqlite` runs `EnsureCreated()`. SQLite is dev-only and ignored in container/k8s deployments. Any other value (typo like `PostgreSQL`, empty string, whitespace) throws `ArgumentException` at silo startup so misconfigured deployments fail fast instead of silently falling back to an in-pod SQLite file.

### Streaming

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Streaming__Provider` | `Fleans:Streaming:Provider` | `Fleans.ServiceDefaults/FleanStreamingExtensions.cs:18` | `"memory"` (case-insensitive; accepts `memory` or `kafka`) |
| `Fleans__Streaming__Kafka__Brokers` | `Fleans:Streaming:Kafka:Brokers` | `Fleans.ServiceDefaults/FleanStreamingExtensions.cs:25` (`GetSection("Fleans:Streaming:Kafka")` binds to `Fleans.Streaming.Kafka/KafkaStreamingOptions.cs:8`) | — |
| `Fleans__Streaming__Kafka__ConsumerGroup` | `Fleans:Streaming:Kafka:ConsumerGroup` | (binding) | `"fleans"` |
| `Fleans__Streaming__Kafka__TopicPrefix` | `Fleans:Streaming:Kafka:TopicPrefix` | (binding) | `"fleans-"` |

`memory` is single-silo only — it silently drops cross-silo events. Use `kafka` for any deployment with more than one silo. See [Streaming](/fleans/reference/streaming/) for at-least-once semantics.

### Authentication

Auth keys are **per-host** — the API and the Web admin UI use different OIDC flows (JWT-bearer vs OIDC code-flow), so the keys split cleanly between hosts. Setting an API-only key on the Web silo (or vice-versa) has no effect.

| Env var | Config key | Read at | Applies to |
| --- | --- | --- | --- |
| `Authentication__Authority` | `Authentication:Authority` | `Fleans.Api/Program.cs:28`, `Fleans.Web/Program.cs:55` | **Both hosts.** OIDC issuer URL. Setting this enables JWT enforcement on `/Workflow/*` (API) and OIDC sign-in on the admin UI. Auth disabled when missing. |
| `Authentication__Audience` | `Authentication:Audience` | `Fleans.Api/Program.cs:36` | **API only.** JWT `aud` claim the API requires. Default: `"fleans-api"`. Setting on the Web silo has no effect. |
| `Authentication__ClientId` | `Authentication:ClientId` | `Fleans.Web/Program.cs:56` | **Web only.** OIDC client identifier for the Blazor Server admin UI. Setting on the API has no effect. |
| `Authentication__ClientSecret` | `Authentication:ClientSecret` | `Fleans.Web/Program.cs:82` | **Web only.** OIDC client secret. Source from a Secret/Key Vault, not appsettings, in production. |

See [Authentication](/fleans/reference/authentication/) for the full role-claim plan and reverse-proxy guidance.

### Connection strings

| Env var | Config key | Read at | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings__fleans` | `ConnectionStrings:fleans` | `Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:29` | **Required when `Persistence:Provider=Postgres`.** Workflow command-side database (event store + write model). Throws at startup if missing. |
| `ConnectionStrings__fleans-query` | `ConnectionStrings:fleans-query` | `Fleans.ServiceDefaults/FleansPersistenceExtensions.cs:32` | Optional read-replica for the query-side projection. Falls back to the `fleans` write connection when unset. |
| `ConnectionStrings__orleans-redis` | `ConnectionStrings:orleans-redis` | `Fleans.Api/Program.cs:65`, `Fleans.WorkerHost/Program.cs:36`, `Fleans.CustomWorkerHost/Program.cs:33` | **Required for multi-silo clustering.** Drives `UseRedisClustering(...)` and `AddRedisGrainStorage("PubSubStore", ...)`. Note the name — it's `orleans-redis`, not bare `redis`. |

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

## Drift-guard

Every "Read at" cell above is pinned to a specific source line. If you rename a config key in source, the corresponding doc claim becomes wrong — manual regression test #N (in `CLAUDE.md`'s *Website regression tests*) re-greps each pinned line and fails noisily if the symbol is gone.
