---
title: Persistence Configuration
description: Configuration reference for Fleans database persistence providers (SQLite and PostgreSQL).
sidebar:
  order: 3
---

Fleans supports two persistence providers: **SQLite** (default, for local development) and **PostgreSQL** (for production and load testing). The provider is selected at startup via configuration — no code changes are required to switch.

## Provider Selection

### Via Aspire (recommended for local dev)

Set the `FLEANS_PERSISTENCE_PROVIDER` environment variable before launching Aspire. **Note:** this is an Aspire-only convenience knob — read by `Fleans.Aspire/Program.cs` to decide what to provision and then forwarded to silos as `Persistence__Provider=Postgres`. Self-hosted silos (no Aspire) read `Persistence__Provider` directly — see [Configuration / Tier 1](/fleans/reference/configuration/#tier-1--aspire--sqlite-mode-dev-knobs).

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

## Test parity with SQLite

Every EF/grain-storage test class in `Fleans.Persistence.Tests` is parametrised across both providers. A regression in PostgreSQL behaviour (JSON column, `timestamptz` round-trip, migration script) fails the build the same way a SQLite regression does.

The pattern (see [`Infrastructure/PersistenceTestBase.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Persistence.Tests/Infrastructure/PersistenceTestBase.cs)):

```csharp
[DataTestMethod]
[DataRow(PersistenceProvider.Sqlite)]
[DataRow(PersistenceProvider.Postgres)]
public async Task WriteAndRead_RoundTrip(PersistenceProvider provider)
{
    await using var fixture = await TestFixtureFactory.CreateAsync(provider);
    var storage = new EfCoreSignalCorrelationGrainStorage(fixture.CommandFactory);
    // ... assertions identical for both providers ...
}
```

### Default developer loop (no Docker)

```bash
cd src/Fleans
dotnet test
```

The PostgreSQL rows surface as `Inconclusive` (a non-failing MSTest outcome). SQLite rows execute normally. No Docker is required.

### Reproducing a PostgreSQL-only failure locally

```bash
cd src/Fleans
FLEANS_PG_TESTS=1 dotnet test --filter "TestCategory=Postgres"
```

`Testcontainers.PostgreSql` boots a single `postgres:16-alpine` container per test assembly (lazy start on first PG fixture access). Each parametrised test class owns a fresh `fleans_test_<guid>` database that runs the production `MigrateAsync()` path and is dropped via `DROP DATABASE WITH (FORCE)` on `[ClassCleanup]`. Per-test isolation comes from `TRUNCATE TABLE … RESTART IDENTITY CASCADE` on the model-derived table list.

### CI

The dedicated [`PostgreSQL tests`](https://github.com/nightBaker/fleans/blob/main/.github/workflows/pg-tests.yml) GitHub Actions workflow runs on every PR + push to `main`, sets `FLEANS_PG_TESTS=1`, and executes `dotnet test --filter "TestCategory=Postgres"` against the Testcontainers-managed image.

## See also

- [Observability](/fleans/reference/observability/) — health checks, metrics, logging, tracing, dashboards, alerting
- [Deployment](/fleans/reference/deployment/) — how to wire the `ConnectionStrings:fleans` / `ConnectionStrings:fleans-query` env vars into Docker Compose, Kubernetes, and bare-VM deployments.
- [Self-Hosting on Kubernetes](/fleans/reference/self-hosting/) — bring-your-own Postgres on the Helm chart.
