---
title: Self-host with Docker Compose
description: Install Fleans from a release-asset Docker Compose bundle. Walks through download, run, .env, persistence, upgrade, and troubleshooting.
---

The Fleans release pipeline publishes a Docker Compose bundle on every
`v<SemVer>` tag. This guide walks through the full lifecycle of a Compose-based
deployment: acquire the artifact, run the stack, tune `.env`, back up state,
and upgrade across releases. For non-release-asset topology details (raw
`aspire publish`, port matrix, reverse-proxy patterns), the canonical
reference is [Deployment](/fleans/reference/deployment/).

## Prerequisites

- **Docker Engine 24+** with the Compose v2 plugin (`docker compose version`).
- **~2 GB free disk** for images + Postgres data volume.
- **2 GB RAM** as a working baseline (Orleans + Redis + Postgres + Web + MCP).
- Linux, macOS, or Windows (WSL2). The bundle is shell-friendly; on Windows
  use Git Bash or WSL2 for the unzip + `docker compose` invocations.
- A running Docker daemon with internet pull access to `ghcr.io/nightbaker/*`.

## 1. Download the release bundle

The release workflow attaches `docker-compose-v<VER>.zip` to every GitHub
Release. Download it via the `gh` CLI:

```bash
gh release download v0.1.0-beta --repo nightBaker/fleans -p 'docker-compose-*.zip'
```

Or, without the `gh` CLI, fetch the asset directly:

```bash
curl -LO https://github.com/nightBaker/fleans/releases/download/v0.1.0-beta/docker-compose-v0.1.0-beta.zip
```

:::note[Future-tag references]
The literal `v0.1.0-beta` above is the first published release. Substitute the
current release tag when you run these commands — see the `## Cutting a
Release` runbook in `CLAUDE.md` for the maintainer-side bump cadence.
:::

## 2. Run the stack

```bash
unzip docker-compose-v0.1.0-beta.zip -d fleans
cd fleans
docker compose up -d
docker compose ps
```

`docker compose ps` should show all services as `running` (or `healthy` once
Redis + Postgres start passing their healthchecks). The default port map:

| Service | Container port | Host port | Purpose |
| --- | --- | --- | --- |
| `fleans-web` | 8080 | 8080 | Blazor Server admin UI |
| `fleans-api` | 5000 | (internal — fronted by web) | Orleans Core silo + REST API |
| `fleans-mcp` | 5200 | (internal) | MCP server for AI-agent integration |
| `redis` | 6379 | (internal) | Orleans clustering + grain storage |
| `postgres` | 5432 | (internal) | Workflow event store + read model |

Open [http://localhost:8080](http://localhost:8080) — the admin UI lands on
the dashboard.

## 3. `.env` reference

The bundle ships an `.env` file populated with safe defaults. Override values
before `docker compose up` to customise the install. Variables follow the
.NET hierarchical naming rule — `:` becomes `__` (e.g. `Authentication:Audience`
→ `Authentication__Audience`); see [Configuration](/fleans/reference/configuration/)
for the full convention.

| Variable | Default | Description |
| --- | --- | --- |
| `FLEANS_PERSISTENCE_PROVIDER` | `Postgres` | `Postgres` (default) or `Sqlite`. Sqlite single-replica only; see [Persistence](/fleans/reference/persistence/). |
| `FLEANS_STREAMING_PROVIDER` | `Memory` | `Memory` (in-process) or `Kafka` (durable). When `Kafka`, set `Fleans__Streaming__Kafka__Brokers`. |
| `Fleans__Streaming__Kafka__Brokers` | *(empty)* | Comma-separated brokers, e.g. `kafka:9092,kafka2:9092`. Required when `FLEANS_STREAMING_PROVIDER=Kafka`. |
| `ConnectionStrings__fleans` | *(in-cluster Postgres)* | Override to point at an external Postgres. See §5 below. |
| `ConnectionStrings__fleans-query` | *(same as `fleans`)* | Optional read-replica for the EF projection. |
| `ConnectionStrings__orleans-redis` | `redis:6379` | Orleans clustering + grain storage. |
| `Authentication__Authority` | *(empty — auth disabled)* | OIDC issuer URL. Set together with `Audience`/`ClientId` to enable auth. |
| `Authentication__Audience` | *(empty)* | API audience claim (Fleans.Api JWT validation). |
| `Authentication__ClientId` | *(empty)* | Web admin UI OIDC client ID (Fleans.Web). |
| `Authentication__ClientSecret` | *(empty)* | Web OIDC client secret. **Use a Docker secret in production**; do not commit. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables verbose logs and dev exception page; never set in prod. |
| `Fleans__Role` | *(unset, equivalent to `Combined`)* | `Core`, `Worker`, or `Combined`. Compose ships a single Combined silo by default. |

Inspect the effective env at any time with `docker compose config | less`.

## 4. Persistence

Two named volumes hold workflow state:

- **`postgres-data`** — workflow event store + EF read-model.
  Backed up via:
  ```bash
  docker compose exec -T postgres \
    pg_dump -U fleans fleans > fleans-backup-$(date +%Y%m%d).sql
  ```
  Restore with `docker compose exec -T postgres psql -U fleans fleans < fleans-backup-YYYYMMDD.sql`.
- **`redis-data`** — optional. Redis is configured for `appendonly no` by
  default; Orleans grain state is rebuilt from the Postgres event store on
  silo restart, so wiping Redis is non-destructive. **Never** wipe Postgres
  without a backup.

`docker compose down -v` removes BOTH volumes. Use `docker compose down`
(without `-v`) to stop the stack while preserving state.

## 5. External Postgres

To use a managed Postgres (RDS, Cloud SQL, Aiven), comment out the `postgres`
service in `docker-compose.yml` and point the connection string at the
external host:

```bash
# .env
ConnectionStrings__fleans=Host=my-pg.example.com;Port=5432;Database=fleans;Username=fleans;Password=<secret>;Ssl Mode=Require
```

The schema migrations run automatically on `Fleans.Api` startup (`MigrateAsync()`).
First boot needs the Postgres user to have `CREATE TABLE` privileges; subsequent
boots only need `INSERT/UPDATE/SELECT/DELETE` on the `fleans` database.

## 6. Upgrade path

Across patch / minor releases:

```bash
# Replace the bundle in-place with the new release.
gh release download v0.2.0 --repo nightBaker/fleans -p 'docker-compose-*.zip'
unzip -o docker-compose-v0.2.0.zip -d fleans

cd fleans
docker compose pull
docker compose up -d
```

The new `Fleans.Api` startup runs any pending EF migrations against the
existing Postgres volume. Redis state is rebuilt automatically on silo
re-activation.

For breaking version bumps (e.g. major release), read the GitHub Release
notes — they call out any migration steps Compose itself does not perform
(secret rotations, value renames, removed env vars).

## 7. Troubleshooting

- **Port 8080 already in use.** Override the host-port map in
  `docker-compose.yml` (e.g. `8080:8080` → `9080:8080`) or stop the conflicting
  process. There is no `FLEANS_WEB_PORT` env in the bundled compose; the port
  binding lives in the YAML itself.
- **`fleans-api` exits with `Postgres connection refused`.** Postgres
  health-check hasn't passed yet. Tail logs (`docker compose logs -f postgres
  fleans-api`) and wait — first boot creates the schema and runs migrations,
  which can take 20-40s on slow disks. If it never settles, check
  `ConnectionStrings__fleans` for the right host (`postgres` for in-cluster,
  the external host for managed PG).
- **`fleans-web` returns 502 Bad Gateway.** The Web app waits for the API to
  be reachable. Same root cause as above — let Postgres + API stabilise.
- **`docker compose config` shows blank values for `Authentication__*`.** The
  `.env` file isn't being read. `docker compose` reads `.env` from the same
  directory as the YAML by default; pass `--env-file path/to/.env` if you put
  it elsewhere.
- **Linux SELinux/AppArmor permission denied on volumes.** Add the `:Z` suffix
  to bind-mount paths in `docker-compose.yml` (e.g. `./data:/var/lib/postgresql/data:Z`)
  to relabel for SELinux, or run with `--privileged` in dev.

If a specific error is not covered here, the Aspire-emitted compose YAML
mirrors the production topology in [Deployment](/fleans/reference/deployment/) —
check that page for component-level explanations.

## See also

- [Deployment](/fleans/reference/deployment/) — non-Helm topology overview,
  reverse-proxy patterns, port matrix, bare-VM story.
- [Configuration](/fleans/reference/configuration/) — full env-var matrix
  (Tier 1 dev knobs vs Tier 2 runtime keys), the `:` ↔ `__` naming rule.
- [Persistence](/fleans/reference/persistence/) — Sqlite vs Postgres provider
  selection, external-Postgres connection-string contract, migration model.
- [Authentication](/fleans/reference/authentication/) — OIDC setup for the
  admin UI, JWT validation for the API.
- [Cutting a Release](https://github.com/nightBaker/fleans/blob/main/CLAUDE.md#cutting-a-release) —
  maintainer runbook for publishing a new compose bundle.
