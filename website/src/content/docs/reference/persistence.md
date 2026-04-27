---
title: Persistence Configuration
description: Configuration reference for Fleans database persistence providers (SQLite and PostgreSQL).
sidebar:
  order: 3
---

Fleans supports two persistence providers: **SQLite** (default, for local development) and **PostgreSQL** (for production and load testing). The provider is selected at startup via configuration — no code changes are required to switch.

## Provider Selection

### Via Aspire (recommended for local dev)

Set the `FLEANS_PERSISTENCE_PROVIDER` environment variable before launching Aspire:

```bash
# PostgreSQL (provisions a containerised Postgres instance automatically)
FLEANS_PERSISTENCE_PROVIDER=Postgres dotnet run --project Fleans.Aspire

# SQLite (default — no env var needed)
dotnet run --project Fleans.Aspire
```

### Via `appsettings.json` / environment variable (non-Aspire deployments)

```json
{
  "Persistence": {
    "Provider": "Postgres"
  }
}
```

Or set the equivalent environment variable:

```
Persistence__Provider=Postgres
```

Accepted values (case-insensitive): `Sqlite`, `Postgres`. Default: `Sqlite`.

## SQLite (default)

SQLite requires no extra configuration for local development. Aspire sets a temp-directory path automatically.

| Configuration Key | Description | Default |
|---|---|---|
| `FLEANS_SQLITE_CONNECTION` | SQLite connection string | `DataSource=fleans-dev.db` |
| `FLEANS_QUERY_CONNECTION` | Optional read-side connection string | Same as primary |

**Startup behaviour:** SQLite uses `EnsureCreated()` — schema is created from the EF Core model on first run. There is no migration runner for SQLite.

## PostgreSQL

PostgreSQL requires an accessible Postgres instance and a connection string.

| Configuration Key | Description | Required |
|---|---|---|
| `ConnectionStrings:fleans` | Primary PostgreSQL connection string | Yes |
| `ConnectionStrings:fleans-query` | Read-replica connection string | No |

Example `appsettings.json`:

```json
{
  "Persistence": {
    "Provider": "Postgres"
  },
  "ConnectionStrings": {
    "fleans": "Host=localhost;Database=fleans;Username=fleans;Password=secret"
  }
}
```

**Startup behaviour:** PostgreSQL uses `MigrateAsync()` — EF Core migrations are applied automatically on `Fleans.Api` startup. `Fleans.Web` and `Fleans.Mcp` share the same database but do not run migrations (Aspire's `WaitFor(fleansSilo)` ensures ordering).

**Read replica:** When `ConnectionStrings:fleans-query` is provided, the query `DbContext` connects to the replica. The replica must be provisioned separately (e.g. a PostgreSQL standby). Aspire v1 provisions only the primary `fleans` database; read-replica wiring is tracked in issue [#170](https://github.com/nightBaker/fleans/issues/170).

## Aspire Auto-Provisioning (PostgreSQL)

When `FLEANS_PERSISTENCE_PROVIDER=Postgres`, Aspire provisions a Docker-based Postgres container named `postgres` with a database named `fleans`. Connection strings are injected into all three services (`fleans-core`, `fleans-management`, `fleans-mcp`) automatically — no manual `ConnectionStrings` config is needed in development.
