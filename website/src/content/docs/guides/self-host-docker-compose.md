---
title: Self-host with Docker Compose
description: Install Fleans from a release-asset Docker Compose bundle. Walks through download, run, .env, overrides, persistence, upgrade, and troubleshooting.
---

The Fleans release pipeline publishes a Docker Compose bundle on every
`v<SemVer>` tag. This guide walks through the full lifecycle of a Compose-based
deployment: acquire the artifact, run the stack, tune `.env`, back up state,
and move to a new release. For the ports each process uses, reverse-proxy
patterns and running the images without the bundle, see
[Deployment](/fleans/reference/deployment/).

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
gh release download v0.15.0 --repo nightBaker/fleans -p 'docker-compose-*.zip'
```

Or, without the `gh` CLI, fetch the asset directly:

```bash
curl -LO https://github.com/nightBaker/fleans/releases/download/v0.15.0/docker-compose-v0.15.0.zip
```

:::note[Future-tag references]
The literal `v0.15.0` above is the release this guide was written for. Substitute the
current release tag when you run these commands — see the
[`docs/runbooks/release.md`](https://github.com/nightBaker/fleans/blob/main/docs/runbooks/release.md)
runbook for the maintainer-side bump cadence.
:::

## Verify the images the bundle pulls

The compose bundle itself is just declarative YAML — its bytes aren't signed. The substantive supply-chain risk lives in the four container images the YAML references; those images ARE signed by the release pipeline using cosign keyless signing (Sigstore Fulcio CA). Verify each image before `docker compose up`:

```bash
for SVC in api web worker mcp; do
  cosign verify \
    --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
    --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    ghcr.io/nightbaker/fleans-$SVC:0.15.0
done
```

Each `cosign verify` exits 0 and prints a JSON object containing a `Bundle` block with `tlogEntries` proving the signature was logged to the public Rekor transparency log.

`docker compose pull` does NOT verify signatures by default; for enforcement, run an admission controller (Kyverno + Sigstore policy) or pre-pull via `docker pull --policy=verify` (Docker 25+ with content trust enabled).

## 2. Run the stack

<figure class="arch-diagram" style="--arch-ratio: 1400 / 598">
  <iframe data-arch-src="/fleans/diagrams/fleans-cluster.html" src="/fleans/diagrams/fleans-cluster.html?embed=1" title="Fleans deployment architecture" loading="lazy"></iframe>
  <figcaption>How the services fit together once the stack is up; OIDC, custom workers and the alternative storage/queue backends are optional. <a href="/fleans/diagrams/fleans-cluster.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

```bash
unzip docker-compose-v0.15.0.zip -d fleans
cd fleans
docker compose up -d
docker compose ps
```

`docker compose ps` should show all services as `running`. The default port map:

| Service | Container port | Host port | Purpose |
| --- | --- | --- | --- |
| `fleans-management` | 8080 | 8080 | Blazor Server admin UI |
| `fleans-core` | 8080 | 8081 | Orleans silo in the `Core` role + REST API (`/Definitions`, `/Execution`, `/Instances`, …) |
| `fleans-mcp` | 8080 | (internal) | MCP server for AI-agent integration |
| `fleans-worker` | (none) | (internal) | Worker silo: script tasks and the read-model projector |
| `orleans-redis` | 6379 | (internal) | Orleans clustering, reminders and stream queues |
| `postgres` | 5432 | (internal) | Workflow event store + read model |
| `compose-dashboard` | 18888 | (random) | Aspire dashboard: the logs, traces and metrics of every Fleans container |

Open [http://localhost:8080](http://localhost:8080) for the admin UI.
Programmatic clients call the REST API at <http://localhost:8081>, for example
`curl http://localhost:8081/Definitions`. See the [REST API reference](/fleans/reference/api/).

Check health from the host — the images have no `curl` inside:

```bash
curl -fsS http://localhost:8081/health   # fleans-core
curl -fsS http://localhost:8080/health   # fleans-management
```

### The bundled telemetry dashboard

Every Fleans container sends its logs, traces and metrics over OTLP to
`compose-dashboard`, a [standalone Aspire dashboard](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone)
container (`OTEL_EXPORTER_OTLP_ENDPOINT=http://compose-dashboard:18889`). It keeps the
data in memory only. Its UI port gets a random host port; find it, and the login link
with its token, with:

```bash
docker compose port compose-dashboard 18888
docker compose logs compose-dashboard | grep -i login
```

To send telemetry to your own collector instead, override `OTEL_EXPORTER_OTLP_ENDPOINT`
(and `OTEL_EXPORTER_OTLP_PROTOCOL` if it does not speak gRPC) on every `fleans-*` service — see
[Overriding what `.env` cannot reach](#overriding-what-env-cannot-reach) — and stop the
dashboard with `docker compose stop compose-dashboard`.

## 3. `.env` reference

The bundle ships an `.env` file with version-pinned image references, ports and
randomly generated secrets, so `docker compose up -d` works out of the box. Compose
reads it to fill the `${...}` references in `docker-compose.yaml`; a variable reaches a
container only where the YAML references it. These are the variables in the file:

| Variable | Default | Used for |
| --- | --- | --- |
| `FLEANS_CORE_IMAGE` | `ghcr.io/nightbaker/fleans-api:<release>` | Image of `fleans-core`. |
| `FLEANS_MANAGEMENT_IMAGE` | `ghcr.io/nightbaker/fleans-web:<release>` | Image of `fleans-management` (admin UI). |
| `FLEANS_MCP_IMAGE` | `ghcr.io/nightbaker/fleans-mcp:<release>` | Image of `fleans-mcp`. |
| `FLEANS_WORKER_IMAGE` | `ghcr.io/nightbaker/fleans-worker:<release>` | Image of `fleans-worker`. |
| `FLEANS_CORE_PORT` | `8080` | Container port of `fleans-core` (`HTTP_PORTS`). The host port `8081` is fixed in the YAML. |
| `FLEANS_MANAGEMENT_PORT` | `8080` | Container port of `fleans-management`. The host port `8080` is fixed in the YAML. |
| `FLEANS_MCP_PORT` | `8080` | Container port of `fleans-mcp` (not published on the host). |
| `ORLEANS_REDIS_PASSWORD` | random per release | Password of `orleans-redis`, and in every service's Redis connection string. **Replace it for production.** |
| `POSTGRES_PASSWORD` | random per release | Password of the `postgres` superuser, and in every service's connection string. **Replace it for production, before the first `up`:** Postgres sets the password only when it creates its volume. |
| `CLUSTER_CLUSTER_ID` / `CLUSTER_SERVICE_ID` | `fleans` / `fleans` | `Orleans__ClusterId` / `Orleans__ServiceId` on every service. Change them if two Fleans clusters share one Redis. |
| `AUTH_AUTHORITY` | *(empty — auth off)* | `Authentication__Authority` on `fleans-core`, `fleans-management` and `fleans-mcp`. Alone it makes REST (audience `fleans-api`) and MCP (`fleans-mcp`) require JWT bearer tokens; the admin UI also needs the next two. |
| `AUTH_CLIENT_ID` | *(empty)* | `Authentication__ClientId` on `fleans-management`: the admin UI's OIDC client. |
| `AUTH_CLIENT_SECRET` | *(empty)* | `Authentication__ClientSecret` on `fleans-management`. Keep `.env` out of version control. |

See [Authentication](/fleans/reference/authentication/) for the IdP side. Inspect what
each container gets with `docker compose config`.

### Overriding what `.env` cannot reach

Everything else — `Fleans__Role`, `Persistence__Provider`, the connection strings,
the stream provider, `OTEL_EXPORTER_OTLP_ENDPOINT`, any other
[configuration key](/fleans/reference/configuration/) — is written as a literal in
`docker-compose.yaml`. A line for it in `.env` changes nothing. Put such settings in a
`docker-compose.override.yaml` next to `docker-compose.yaml`; `docker compose` merges
it automatically and its values win. Set a key on every service that reads it:

```yaml
# docker-compose.override.yaml
x-overrides: &overrides
  OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector.example.internal:4317"
  Fleans__Variables__MaxSizeBytes: "2097152"

services:
  fleans-core:
    environment: *overrides
  fleans-worker:
    environment: *overrides
  fleans-management:
    environment: *overrides
  fleans-mcp:
    environment: *overrides
```

Keep the override file when you move to a new bundle, and check it against the new
`docker-compose.yaml`.

## 4. Persistence and scaling

The bundle uses Postgres as the workflow event store + read-model, backed by a
single named volume that Aspire generates (the volume name embeds
an Aspire-internal hash, e.g. `fleans.aspire-<hash>-postgres-data`). Find the
exact name with:

```bash
docker volume ls --filter name=postgres-data
```

To back up and restore the database (the bundle's Postgres requires its password even on
local connections), follow [Backup and restore](/fleans/guides/backup-restore/).

Redis holds Orleans cluster membership, reminders (pending BPMN timers) and the
stream queues. The bundle gives the Redis container no volume: Redis saves its
data with its default settings to the container's own disk, so a plain restart
(`docker compose restart orleans-redis`, a host reboot) keeps it. Removing or
recreating the container — `docker compose down`, an image or settings change —
**loses pending timers and undelivered stream events**, so in-flight instances
can stall. Recreate Redis only with no instances in flight, or move reminders
and streams off Redis for production
(see [Configuration → Redis](/fleans/reference/configuration/#redis)). There is no stream
pub-sub state to lose: subscriptions are implicit only.
**Never** wipe Postgres without a backup.

`docker compose down -v` removes the Postgres volume. Use `docker compose down`
(without `-v`) to stop the stack while preserving state.

### Scaling

To tune Orleans consumer parallelism (pulling-agent count per cluster) for higher throughput, set `Fleans__Streaming__Redis__TotalQueueCount` (Redis), `Fleans__Streaming__Kafka__QueueCount` (Kafka), or an explicit `Fleans__Streaming__AzureQueue__QueueNames__0..N` list on both silo services (`fleans-core`, `fleans-worker`) in your [override file](#overriding-what-env-cannot-reach). Keep the queue count identical across all silos, and change it only with every silo stopped. See [Tuning throughput](/fleans/reference/streaming/#tuning-throughput) for the sizing heuristic, rehash caveat, and the one-partition-per-topic rule for Kafka.

Add Worker silos with:

```bash
docker compose up -d --scale fleans-worker=3
```

`fleans-worker` publishes no host port, so its replicas do not collide. `fleans-core` binds
host port `8081` and cannot be scaled this way; for more Core silos, see
[Recommended for scale](/fleans/guides/topology-scalable/).

## 5. External Postgres

To use a managed Postgres (RDS, Cloud SQL, Aiven), point every service's
connection string at it in your [override file](#overriding-what-env-cannot-reach):

```yaml
# docker-compose.override.yaml
x-db: &db
  ConnectionStrings__fleans: "Host=my-pg.example.com;Port=5432;Database=fleans;Username=fleans;Password=<secret>;Ssl Mode=Require;Maximum Pool Size=20"

services:
  fleans-core:
    environment: *db
  fleans-worker:
    environment: *db
  fleans-management:
    environment: *db
  fleans-mcp:
    environment: *db
```

The bundled `postgres` container then runs unused. To drop it, delete the `postgres`
service, its volume and the `depends_on: postgres` entries from `docker-compose.yaml`.
Size `Maximum Pool Size` with the [connection budget](/fleans/reference/persistence/#connection-budget).

Every Fleans service applies the schema migrations at startup (serialized by an advisory
lock), so the database user needs the right to create tables in the `fleans` database,
not only to read and write rows.

## 6. Moving to a new release

Until 1.0.0 a new release starts on fresh, empty stores — there is no in-place upgrade.
Running instances and deployed definitions of the old release do not carry over. The
steps (drain, back up, `docker compose down -v`, unpack the new bundle, `up`) are in
[Upgrading](/fleans/guides/upgrading/#docker-compose-bundle).

Within one release, `docker compose up -d` after editing `.env` or the override file
recreates only the services whose settings changed.

## 7. Troubleshooting

- **Port 8080 already in use.** Edit the host port in `docker-compose.yaml` —
  e.g. `8080:${FLEANS_MANAGEMENT_PORT}` → `9080:${FLEANS_MANAGEMENT_PORT}` —
  or stop the conflicting process. The container-side port stays unchanged.
- **`fleans-core` restarts a few times before becoming stable.** The Postgres
  container takes 20–40s to initialise on first run. The Fleans services use Compose's
  `restart: on-failure` policy and retry connecting until Postgres is ready. As
  long as `docker compose ps` eventually shows them all as `Up` and stable,
  this is expected. Watch with `docker compose logs -f fleans-core postgres`.
- **Migration errors like `relation "X" already exists`.** All four Fleans
  services (`fleans-core`, `fleans-management`, `fleans-mcp`, `fleans-worker`)
  apply the schema at startup and serialize on a Postgres advisory lock
  (transaction-scoped, so it also holds through PgBouncer in transaction mode).
  If it appears after switching releases, the database still holds the
  schema of the old one: start the new release on a fresh database — see
  [Upgrading](/fleans/guides/upgrading/).
- **`docker compose config` shows blank values for `Authentication__*`.** The
  bundle ships auth off. Set `AUTH_AUTHORITY`, `AUTH_CLIENT_ID` and
  `AUTH_CLIENT_SECRET` in `.env` (not `Authentication__*` lines, which Compose
  does not pass on) and run `docker compose up -d`.
- **Linux SELinux permission denied on a bind mount you added.** Add the `:Z`
  suffix to the bind-mount path (e.g. `./backups:/backups:Z`) to relabel it.

For the components and ports each service uses, see
[Deployment](/fleans/reference/deployment/).

## See also

- [Deployment](/fleans/reference/deployment/) — components, ports per process,
  reverse-proxy patterns, running the images without the bundle.
- [Configuration](/fleans/reference/configuration/) — every configuration key and
  the `:` ↔ `__` naming rule.
- [Upgrading](/fleans/guides/upgrading/) — moving to a new release before 1.0.
- [Persistence](/fleans/reference/persistence/) — Sqlite vs Postgres provider
  selection, external-Postgres connection-string contract, migration model.
- [Authentication](/fleans/reference/authentication/) — OIDC setup for the
  admin UI, JWT validation for the API.
- [Cutting a Release](https://github.com/nightBaker/fleans/blob/main/docs/runbooks/release.md) —
  maintainer runbook for publishing a new compose bundle.
