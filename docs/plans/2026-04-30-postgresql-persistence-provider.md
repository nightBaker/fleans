# PostgreSQL persistence provider — design snapshot

**Status:** Frozen snapshot of the v2 design plan that landed across PRs [#269](https://github.com/nightBaker/fleans/pull/269) and [#281](https://github.com/nightBaker/fleans/pull/281). Parent issue [#236](https://github.com/nightBaker/fleans/issues/236) is closed; ongoing user-facing changes belong in [`website/src/content/docs/reference/persistence.md`](../../website/src/content/docs/reference/persistence.md), not this file.

This document is a frozen snapshot. It records the architecture and decisions as merged on `main` so future readers do not have to reconstruct them from issue threads. For "how do I use it" content, see the [reference docs](../../website/src/content/docs/reference/persistence.md).

---

## 1. Context

The PostgreSQL provider was added so Fleans could serve as a real backend for production deployments and load-testing scenarios where SQLite's single-file model cannot scale ([#236](https://github.com/nightBaker/fleans/issues/236), [#168](https://github.com/nightBaker/fleans/issues/168)). SQLite stays the default for local development because it requires no container, no network, and no extra setup — `dotnet run --project Fleans.Aspire` still works out of the box.

Two providers are kept side by side instead of one being deprecated. Selection is configuration-only, so a developer can run SQLite locally and the same binary can run PostgreSQL in CI or production.

## 2. Provider selection

Selection happens in [`FleansPersistenceExtensions.AddFleansPersistence`](../../src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs) at host startup. All three host projects (`Fleans.Api`, `Fleans.Web`, `Fleans.Mcp`) call this extension uniformly.

- **Config key:** `Persistence:Provider`
- **Accepted values (case-insensitive):** `Sqlite`, `Postgres`
- **Default:** `Sqlite` (matches the value used before the provider split, so existing deployments keep working untouched)

Any other (non-empty) value falls back to SQLite. The fallback is silent and is tracked as an audit follow-up: [#413 — `FleansPersistenceExtensions` silently falls back to SQLite for unknown provider values](https://github.com/nightBaker/fleans/issues/413).

## 3. Per-provider model customizer pattern

Both providers expose a `RelationalModelCustomizer` subclass, registered via `ReplaceService<IModelCustomizer, …>()` on the EF Core `DbContextOptionsBuilder`:

- [`SqliteModelCustomizer`](../../src/Fleans/Fleans.Persistence.Sqlite/SqliteModelCustomizer.cs) — applies `DateTimeOffset → ISO 8601 string` value converters to all `DateTimeOffset` columns (`WorkflowInstanceState.{CreatedAt, ExecutionStartedAt, CompletedAt}`, `UserTaskState.CreatedAt`, `ProcessDefinition.DeployedAt`).
- [`PostgresModelCustomizer`](../../src/Fleans/Fleans.Persistence.PostgreSql/PostgresModelCustomizer.cs) — no overrides for v1; it exists only so EF Core's model-cache key is provider-distinct, preventing cross-provider model bleed in processes that may register both providers (e.g. integration tests).

The customizer was chosen over a DI-resolved configurator interface because EF Core's model-cache key already incorporates `DbContextOptionsExtensions`. Replacing the customizer service guarantees per-provider models are isolated without an extra abstraction. Ordering is deterministic: `base.Customize()` runs the shared `FleanModelConfiguration` first, then provider-specific tweaks.

## 4. `DateTimeOffset` storage strategy

| Provider   | Storage column type        | Where the conversion happens                         |
|------------|----------------------------|------------------------------------------------------|
| SQLite     | `TEXT` (ISO 8601 string)   | `SqliteModelCustomizer` value converters             |
| PostgreSQL | `timestamp with time zone` | Native (no converter — Npgsql maps `DateTimeOffset`) |

The shared model in `Fleans.Persistence.FleanModelConfiguration` stores `DateTimeOffset` natively. SQLite needs the string converter because it has no first-class temporal type with timezone, and an unconverted `DateTimeOffset` causes "ORDER BY DateTimeOffset not supported" errors (the existing tests for ordering-by-`CreatedAt` cover this regression). PostgreSQL uses `timestamptz` directly.

When adding a new provider, decide which side of this line it sits on and document the choice next to its model customizer.

## 5. Migration approach

| Provider   | Schema bootstrap                                    | Migrations live in                                   |
|------------|-----------------------------------------------------|------------------------------------------------------|
| SQLite     | `EnsureCreated()` (with optional `Migrate()` opt-in)| `Fleans.Persistence.Sqlite/Migrations/Command/`      |
| PostgreSQL | `MigrateAsync()` on `Fleans.Api` startup            | `Fleans.Persistence.PostgreSql/Migrations/Command/`  |

Only the **command** context has maintained migrations. The query context shares the same physical database; its read model is a projection of command tables and does not own schema changes.

For SQLite hosts that opt into migrations, [`BaselineMigrations.MarkInitialAsApplied`](../../src/Fleans/Fleans.Persistence.Sqlite/) idempotently records the `Initial` migration as applied for databases originally created by `EnsureCreated()`, so flipping a long-lived dev database from `EnsureCreated()` to `Migrate()` does not retry the initial schema and crash on duplicates.

For PostgreSQL, all three hosts (Api, Web, Mcp) call `EnsureDatabaseSchemaAsync` at startup. `MigrateAsync` is idempotent and EF Core serializes concurrent migration attempts via the `__EFMigrationsLock` table, so the calls in Web and Mcp are safe; under Aspire they are no-ops because both wait for `Fleans.Api` to come up first.

## 6. Connection strings

| Provider   | Primary key                       | Read-replica key                       | Required?                |
|------------|-----------------------------------|----------------------------------------|--------------------------|
| SQLite     | `FLEANS_SQLITE_CONNECTION` env    | `FLEANS_QUERY_CONNECTION` env          | Optional (default works) |
| PostgreSQL | `ConnectionStrings:fleans`        | `ConnectionStrings:fleans-query`       | Primary required         |

When the PostgreSQL primary string is missing, `AddFleansPersistence` throws `InvalidOperationException("Connection string 'fleans' is required when Persistence:Provider=Postgres")`. The SQLite primary defaults to `DataSource=fleans-dev.db`.

`fleans-query` is consumed by the read-side `DbContext` only. It is provisioned out-of-band (e.g. a PostgreSQL standby); Aspire does not provision a replica today. Read-replica wiring is tracked in [#170](https://github.com/nightBaker/fleans/issues/170).

## 7. Aspire auto-provisioning

`Fleans.Aspire/Program.cs` reads `FLEANS_PERSISTENCE_PROVIDER` at startup with `StringComparison.OrdinalIgnoreCase`:

- Unset / `Sqlite` (default) — no container is provisioned. Apps run with the SQLite default and a temp-directory connection string injected by Aspire.
- `Postgres` — Aspire calls `builder.AddPostgres("postgres").WithDataVolume().AddDatabase("fleans")`, references the resource into all three project resources, and stamps `Persistence__Provider=Postgres` onto each project's environment. `Fleans.Api` adds an explicit `WaitFor(pg)`; Web and Mcp wait on `Fleans.Api` (the silo), which transitively waits on Postgres.

The default-stays-SQLite policy is deliberate: F5 from the IDE must keep working without Docker.

## 8. Testing strategy

PostgreSQL integration tests use [Testcontainers](https://dotnet.testcontainers.org/) — the test process owns the container lifecycle. This is the central decision behind the (still-open) Phase 4 work in [#278](https://github.com/nightBaker/fleans/issues/278) and the (still-open) Phase 5b CI work tracked separately.

**Why Testcontainers and not GitHub Actions `services: postgres:`:**

- `services:` and Testcontainers both want to own port `5432` on the runner. Pinning ports collides; not pinning means tests have to discover the host port, which Testcontainers already solves transparently.
- A Testcontainers-driven setup is identical between a developer's laptop and CI. There is no "works in CI, not locally" mode.
- The test fixture controls container readiness (Postgres is fully booted before the first connection), without an extra `wait-for-it` script.

**Test selection contract:**

- Postgres-only tests are tagged `[TestCategory("Postgres")]`.
- The CI workflow filters with `dotnet test --filter "TestCategory=Postgres"` and sets `FLEANS_PG_TESTS=1` so the Testcontainers fixture activates.
- Locally, a developer runs `dotnet test` (default — Postgres tests are skipped without `FLEANS_PG_TESTS=1`) or `FLEANS_PG_TESTS=1 dotnet test --filter "TestCategory=Postgres"` (Postgres lane only, requires Docker).

**Phase 5b silent-green guard (landed):** `dotnet test --filter "TestCategory=Postgres"` matching zero tests passes silently. The Phase 5b CI workflow runs `dotnet test` with `--logger "trx;LogFileName=pg.trx"`, then asserts via `xmllint` that the TRX's `<ResultSummary>/<Counters/@total>` is greater than zero (with a `count(//UnitTestResult) > 0` belt-and-braces fallback). **Failure mode:** the job fails (with a clear log message naming the likely root cause — the `[TestCategory("Postgres")]` attribute being dropped from the parametrised classes) if zero tests were considered. Tracked at [#445](https://github.com/nightBaker/fleans/issues/445); see `.github/workflows/pg-tests.yml` for the implementation.

## 9. Phase log

| Phase | Issue | Delivery                                                              | Notes |
|-------|-------|-----------------------------------------------------------------------|-------|
| 1     | (no tracking issue — predates the phased rollout) | [PR #269](https://github.com/nightBaker/fleans/pull/269) — extract SQLite into `Fleans.Persistence.Sqlite` (pure refactor, no schema change) | Established the per-provider package layout that Phases 2–5 build on |
| 2     | [#276](https://github.com/nightBaker/fleans/issues/276) | [PR #281](https://github.com/nightBaker/fleans/pull/281), merge commit `9c719d7` | Initial SQLite migrations + `BaselineMigrations` helper |
| 3     | [#277](https://github.com/nightBaker/fleans/issues/277) | [PR #281](https://github.com/nightBaker/fleans/pull/281) | `Fleans.Persistence.PostgreSql` provider, host wiring, Aspire provisioning |
| 4     | [#278](https://github.com/nightBaker/fleans/issues/278) | _Open_ | Testcontainers-driven PG integration tests |
| 5     | [#279](https://github.com/nightBaker/fleans/issues/279) | This doc + already-shipped [`reference/persistence.md`](../../website/src/content/docs/reference/persistence.md) | Public docs page (Step 18) shipped earlier; this doc is Step 19 |
| 5b    | _follow-up issue_ | _Open_ | CI workflow (`.github/workflows/postgres-tests.yml`) — depends on #278; must include the silent-green guard |

**#277 board drift:** PR #281's body listed `Closes #276 and #277` in prose, but only `#276` appeared in GitHub's `closingIssuesReferences` (the `Closes #` shorthand was applied to one number, not both). As a result #277 was not auto-closed at merge, and the project board entry stayed in Backlog. This was noticed during Phase 5 design review and corrected manually. Future multi-issue PRs should write `Closes #276` *and* `Closes #277` on separate lines (or use one `Closes #N` per issue) so every linked issue ends up in `closingIssuesReferences`.

## 10. Adding a new provider

Mirror the per-provider shape established by SQLite and PostgreSQL — this checklist matches the entry in [`docs/conventions/persistence.md`](../conventions/persistence.md#adding-a-new-provider):

1. Create a new `Fleans.Persistence.<Provider>` class library that references `Fleans.Persistence` (shared model) and the EF Core provider package.
2. Add a `<Provider>ModelCustomizer : RelationalModelCustomizer` that handles whatever the shared model cannot store natively for that engine (string conversion, JSON shape, etc.). Document that decision in a class-level XML comment, the way `SqliteModelCustomizer` and `PostgresModelCustomizer` do.
3. Add an `Add<Provider>Persistence(this IServiceCollection, string connectionString, string? queryConnectionString = null)` extension. Register the customizer via `ReplaceService<IModelCustomizer, <Provider>ModelCustomizer>()` so EF Core's model cache stays per-provider.
4. Generate initial migrations into `Fleans.Persistence.<Provider>/Migrations/Command/`. Maintain only the command context — query context shares the same database.
5. Wire the new provider into `FleansPersistenceExtensions.AddFleansPersistence` behind a new `Persistence:Provider` value (case-insensitive). Add an explicit branch — do not fall through silently.
6. If schema bootstrap differs from `EnsureCreated`/`MigrateAsync`, extend `EnsureDatabaseSchemaAsync` with a corresponding branch.
7. Update [`website/src/content/docs/reference/persistence.md`](../../website/src/content/docs/reference/persistence.md) with provider selection, connection-string keys, and startup behaviour. Update Aspire if container auto-provisioning is desired.
8. Add tests under `[TestCategory("<Provider>")]` and a corresponding env-var gate so the lane stays opt-in by default.

## 11. Known follow-ups

- **`jsonb` migration for PostgreSQL** — JSON columns currently land as `text` so the layout matches SQLite. Migrating to `jsonb` is deferred until there is a concrete read pattern that benefits from it (tracked under [#236](https://github.com/nightBaker/fleans/issues/236) follow-ups).
- **Audit: silent provider fallback** — [#413](https://github.com/nightBaker/fleans/issues/413). `FleansPersistenceExtensions` should fail fast on an unknown `Persistence:Provider` value instead of silently falling back to SQLite.
- **Audit: Aspire role injection** — [#416](https://github.com/nightBaker/fleans/issues/416). `Fleans__Role` is currently chart-only and not injectable through Aspire's `WithEnvironment`. Independent of persistence, but discovered while reviewing the same set of host extensions.
- **Read replica wiring** — [#170](https://github.com/nightBaker/fleans/issues/170). The read-side `DbContext` already supports `ConnectionStrings:fleans-query`; what is missing is Aspire provisioning of the replica + the production guidance for promoting/failing-over.
- **Phase 5b — CI workflow** — open follow-up issue. Body includes the silent-green guard requirement (see §8) and links to this doc by path so the implementation has a single source of truth.
