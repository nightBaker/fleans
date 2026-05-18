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

## Verify the images the bundle pulls

The compose bundle itself is just declarative YAML — its bytes aren't signed. The substantive supply-chain risk lives in the four container images the YAML references; those images ARE signed by the release pipeline using cosign keyless signing (Sigstore Fulcio CA). Verify each image before `docker compose up`:

```bash
for SVC in api web worker mcp; do
  cosign verify \
    --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
    --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    ghcr.io/nightbaker/fleans-$SVC:0.1.0-beta
done
```

Each `cosign verify` exits 0 and prints a JSON object containing a `Bundle` block with `tlogEntries` proving the signature was logged to the public Rekor transparency log.

`docker compose pull` does NOT verify signatures by default; for enforcement, run an admission controller (Kyverno + Sigstore policy) or pre-pull via `docker pull --policy=verify` (Docker 25+ with content trust enabled).

## 2. Run the stack

```bash
unzip docker-compose-v0.1.0-beta.zip -d fleans
cd fleans
docker compose up -d
docker compose ps
```

`docker compose ps` should show all services as `running`. The default port map:

| Service | Container port | Host port | Purpose |
| --- | --- | --- | --- |
| `fleans-management` | 8080 | 8080 | Blazor Server admin UI |
| `fleans-core` | 8080 | 8081 | Orleans Core silo + REST API (`/workflow/*`) |
| `fleans-mcp` | 8080 | (internal) | MCP server for AI-agent integration |
| `fleans-worker` | (none) | (internal) | Worker silo (script/condition grains, custom-task plugins) |
| `orleans-redis` | 6379 | (internal) | Orleans clustering + PubSub grain storage |
| `postgres` | 5432 | (internal) | Workflow event store + read model |
| `compose-dashboard` | 18888 | (random) | Aspire dashboard for OTel traces (dev tool) |

Open [http://localhost:8080](http://localhost:8080) — the admin UI lands on
the dashboard. Programmatic clients hit the REST API at
[http://localhost:8081/workflow/...](http://localhost:8081).

## 3. `.env` reference

The bundle ships an `.env` file populated with sensible defaults — the release
pipeline post-processes Aspire's compose output to bake in version-pinned image
refs, randomly generated secrets, and conventional ports so `docker compose up
-d` works out of the box. Override values before bringing the stack up to
customise the install. Variables follow the .NET hierarchical naming rule — `:`
becomes `__` (e.g. `Authentication:Audience` → `Authentication__Audience`); see
[Configuration](/fleans/reference/configuration/) for the full convention.

| Variable | Default | Description |
| --- | --- | --- |
| `FLEANS_CORE_IMAGE` | `ghcr.io/nightbaker/fleans-api:<release-tag>` | Orleans Core silo image. Change to pin a different version or registry. |
| `FLEANS_MANAGEMENT_IMAGE` | `ghcr.io/nightbaker/fleans-web:<release-tag>` | Blazor admin UI image. |
| `FLEANS_MCP_IMAGE` | `ghcr.io/nightbaker/fleans-mcp:<release-tag>` | MCP server image. |
| `FLEANS_WORKER_IMAGE` | `ghcr.io/nightbaker/fleans-worker:<release-tag>` | Worker silo image. |
| `FLEANS_CORE_PORT` | `8080` | Container port the API listens on. The bundle maps host `8081 → container 8080`. |
| `FLEANS_MANAGEMENT_PORT` | `8080` | Container port the Web UI listens on. The bundle maps host `8080 → container 8080`. |
| `FLEANS_MCP_PORT` | `8080` | Container port the MCP server listens on (no host binding by default). |
| `ORLEANS_REDIS_PASSWORD` | *(random per release)* | **Override in production.** Defaults to a random secret baked into `.env` at release time, identical across silos. |
| `POSTGRES_PASSWORD` | *(random per release)* | **Override in production.** Same caveat as the redis password. |
| `CLUSTER_CLUSTER_ID` / `CLUSTER_SERVICE_ID` | `fleans` / `fleans` | Orleans cluster identity. Change if running multiple Fleans clusters against shared Redis/Postgres. |
| `Authentication__Authority` | *(empty — auth disabled)* | OIDC issuer URL. Set together with `ClientId`/`ClientSecret` to enable auth. |
| `Authentication__ClientId` | *(empty)* | Web admin UI OIDC client ID (Fleans.Web). |
| `Authentication__ClientSecret` | *(empty)* | Web OIDC client secret. **Use a Docker secret in production**; do not commit. |
| `Fleans__Role` | `Core` (api) / `Worker` (worker) | Internal role per service. Don't change unless you understand the [Core/Worker split](/fleans/reference/deployment/). |

Inspect the effective env at any time with `docker compose config | less`.

## 4. Persistence

The bundle uses Postgres as the workflow event store + read-model, backed by a
single named volume that Aspire generates per release (the volume name embeds
an Aspire-internal hash, e.g. `fleans.aspire-<hash>-postgres-data`). Find the
exact name with:

```bash
docker volume ls --filter name=postgres-data
```

Back up the database with `pg_dump`:

```bash
docker compose exec -T postgres \
  pg_dump -U postgres fleans > fleans-backup-$(date +%Y%m%d).sql
```

Restore with `docker compose exec -T postgres psql -U postgres fleans < fleans-backup-YYYYMMDD.sql`.

Redis (Orleans clustering + PubSub grain storage) is in-memory only — the
bundle does not configure persistence on the Redis container. Orleans
re-bootstraps cluster membership and PubSub state from Postgres + the Redis
clustering provider on silo restart, so a Redis wipe is non-destructive.
**Never** wipe Postgres without a backup.

`docker compose down -v` removes the Postgres volume. Use `docker compose down`
(without `-v`) to stop the stack while preserving state.

To tune Orleans consumer parallelism (pulling-agent count per cluster) for higher throughput, set `Fleans__Streaming__Redis__TotalQueueCount` (Redis), `Fleans__Streaming__Kafka__QueueCount` (Kafka), or supply an explicit `Fleans__Streaming__AzureQueue__QueueNames__0..N` list in `.env`. See [Tuning throughput](/fleans/reference/streaming/#tuning-throughput) for the sizing heuristic, rehash caveat, and Kafka-side `NumPartitions` tuning.

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

- **Port 8080 already in use.** Edit the host port in `docker-compose.yaml` —
  e.g. `8080:${FLEANS_MANAGEMENT_PORT}` → `9080:${FLEANS_MANAGEMENT_PORT}` —
  or stop the conflicting process. The container-side port stays unchanged.
- **`fleans-core` restarts a few times before becoming stable.** The Postgres
  container takes 20–40s to initialise on first run. The silos use Compose's
  `restart: always` policy and retry connecting until Postgres is ready. As
  long as `docker compose ps` eventually shows them all as `Up` and stable,
  this is expected. Watch with `docker compose logs -f fleans-core postgres`.
- **Migration errors like `relation "X" already exists`.** All silos
  (`fleans-core`, `fleans-management`, `fleans-mcp`, `fleans-worker`) call
  `MigrateAsync` at startup; they serialize via a Postgres advisory lock so
  concurrent migrations are safe. If you ever see a "relation already exists"
  error, the lock acquisition was likely skipped (e.g. running an out-of-tree
  fork without the fix shipped in `Fleans.ServiceDefaults`); upgrade to a
  release ≥ 0.1.0-beta.
- **`docker compose config` shows blank values for `Authentication__*`.** The
  bundle ships those empty by default (auth-disabled mode). Fill them in `.env`
  to enable OIDC.
- **Linux SELinux/AppArmor permission denied on volumes.** Add the `:Z` suffix
  to bind-mount paths in `docker-compose.yaml` (e.g. `./data:/var/lib/postgresql/data:Z`)
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
