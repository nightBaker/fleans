# Compose-bundle post-processing

`aspire publish -t docker-compose` emits a YAML that is structurally correct but unusable out of the box:

- Every parameter (image refs, ports, secrets) ships as an empty `${VAR}` placeholder.
- Host-port mappings are random.
- `POSTGRES_DB` is missing, so Aspire's `AddDatabase("fleans")` connection string targets a database that never gets created.
- There are no `depends_on` / healthcheck declarations, so on cold start `fleans-core` can race postgres and crash its EF Core migration with `Connection refused` (the .NET process then zombies at 99% CPU because other DI services prevent clean exit, leaving the container "Up" but unresponsive — discovered while smoke-testing v0.1.0).

The release pipeline runs `src/Fleans/scripts/postprocess-compose-bundle.sh out/compose <version>` after `aspire publish` to fix all of this.

## What the script does

1. Fills `.env` with sensible defaults (version-pinned ghcr.io image refs, container ports `8080`, random base64 redis/postgres passwords, cluster id `fleans`).
2. Rewrites `ports:` to fixed host bindings (Web on `8080`, API on `8081`).
3. Injects `POSTGRES_DB: fleans` on the postgres service.
4. Adds a `pg_isready -U postgres -d fleans` healthcheck on postgres.
5. Upgrades Aspire's `depends_on { postgres: condition: service_started }` to `condition: service_healthy` so the healthcheck is actually consulted.
6. Adds `restart: on-failure` to every `fleans-*` service so they self-heal on transient failures.

## Idempotency

The script is idempotent:

- Fills lines that match `KEY=$`.
- Skips the postgres healthcheck if one already exists.
- Skips `restart:` injection per-service if already present.
- The §5 rewrite is a no-op once the condition has been upgraded.

## Rule for new Aspire parameters

**Any new Aspire-emitted parameter that ships empty must get a default added here**, otherwise the artifact is broken at first run.
