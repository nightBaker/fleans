---
title: Configuration
description: Canonical reference for Fleans configuration keys and their environment-variable equivalents — covers the .NET hierarchical naming rule and the production runtime-key surface.
sidebar:
  order: 0
---

This page is the canonical lookup for every Fleans configuration key, what it does, and which environment variable equivalent operators set on a container or systemd unit. If you're trying to figure out why `Persistence__Provider` isn't taking effect on a production silo, or whether `Authentication__ClientId` belongs on the API or the Web host, this is the right page.

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

The provider toggle. `Postgres` runs `MigrateAsync()` at startup; `Sqlite` runs `EnsureCreated()`. SQLite is dev-only and ignored in container/k8s deployments. Any other value (typo like `PostgreSQL`, empty string, whitespace) throws `ArgumentException` at silo startup so misconfigured deployments fail fast instead of silently falling back to an in-pod SQLite file.

### Streaming

| Env var | Config key | Read at | Default |
| --- | --- | --- | --- |
| `Fleans__Streaming__Provider` | `Fleans:Streaming:Provider` | `Fleans.ServiceDefaults` | `"memory"` (case-insensitive; accepts `memory` or `kafka`) |
| `Fleans__Streaming__Kafka__Brokers` | `Fleans:Streaming:Kafka:Brokers` | `Fleans.ServiceDefaults` (binding) | — |
| `Fleans__Streaming__Kafka__ConsumerGroup` | `Fleans:Streaming:Kafka:ConsumerGroup` | (binding) | `"fleans"` |
| `Fleans__Streaming__Kafka__TopicPrefix` | `Fleans:Streaming:Kafka:TopicPrefix` | (binding) | `"fleans-"` |

`memory` is single-silo only — it silently drops cross-silo events. Use `kafka` for any deployment with more than one silo. See [Streaming](/fleans/reference/streaming/) for at-least-once semantics.

### Authentication

Auth keys are **per-host** — the API and the Web admin UI use different OIDC flows (JWT-bearer vs OIDC code-flow), so the keys split cleanly between hosts. Setting an API-only key on the Web silo (or vice-versa) has no effect.

| Env var | Config key | Read at | Applies to |
| --- | --- | --- | --- |
| `Authentication__Authority` | `Authentication:Authority` | `Fleans.Api`, `Fleans.Web` | **Both hosts.** OIDC issuer URL. Setting this enables JWT enforcement on `/Workflow/*` (API) and OIDC sign-in on the admin UI. Auth disabled when missing. |
| `Authentication__Audience` | `Authentication:Audience` | `Fleans.Api` | **API only.** JWT `aud` claim the API requires. Default: `"fleans-api"`. Setting on the Web silo has no effect. |
| `Authentication__ClientId` | `Authentication:ClientId` | `Fleans.Web` | **Web only.** OIDC client identifier for the Blazor Server admin UI. Setting on the API has no effect. |
| `Authentication__ClientSecret` | `Authentication:ClientSecret` | `Fleans.Web` | **Web only.** OIDC client secret. Source from a Secret/Key Vault, not appsettings, in production. |

See [Authentication](/fleans/reference/authentication/) for the full role-claim plan and reverse-proxy guidance.

### Connection strings

| Env var | Config key | Read at | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings__fleans` | `ConnectionStrings:fleans` | `Fleans.ServiceDefaults` | **Required when `Persistence:Provider=Postgres`.** Workflow command-side database (event store + write model). Throws at startup if missing. |
| `ConnectionStrings__fleans-query` | `ConnectionStrings:fleans-query` | `Fleans.ServiceDefaults` | Optional read-replica for the query-side projection. Falls back to the `fleans` write connection when unset. |
| `ConnectionStrings__orleans-redis` | `ConnectionStrings:orleans-redis` | `Fleans.Api`, `Fleans.WorkerHost`, `Fleans.CustomWorkerHost` | **Required for multi-silo clustering.** Drives `UseRedisClustering(...)` and `AddRedisGrainStorage("PubSubStore", ...)`. Note the name — it's `orleans-redis`, not bare `redis`. |

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
