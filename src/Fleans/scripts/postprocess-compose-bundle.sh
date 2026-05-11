#!/usr/bin/env bash
# Post-process the output of `aspire publish -t docker-compose` so the resulting bundle is
# usable out of the box: fill .env defaults, rewrite host port bindings to fixed mappings.
#
# Aspire's compose publisher leaves every parameter empty in .env, expecting the operator
# to fill them in before `docker compose up`. That's a poor first-run experience for a
# release-asset bundle, so this script bakes in sensible defaults (random secrets,
# version-pinned image refs, host:container port mappings).
#
# Usage:
#   ./postprocess-compose-bundle.sh <bundle-dir> <version>
#
# Arguments:
#   bundle-dir : Directory containing docker-compose.yaml + .env produced by aspire publish.
#   version    : Release version (e.g. 0.1.0-beta) — used to tag the GHCR image refs.
#
# Idempotent: only overwrites placeholder lines (`KEY=$`); pre-filled values are preserved
# so operators can re-run aspire publish + this script without losing local edits.
set -euo pipefail

bundle_dir="${1:?bundle-dir required}"
version="${2:?version required}"
env_file="$bundle_dir/.env"
yaml_file="$bundle_dir/docker-compose.yaml"

[ -f "$env_file" ]  || { echo "::error::no .env at $env_file"; exit 1; }
[ -f "$yaml_file" ] || { echo "::error::no docker-compose.yaml at $yaml_file"; exit 1; }

# 1) Image refs — derived from release version. Container repo names track the
#    <ContainerRepository> values in the project csproj files.
fill_default() {
  local key="$1" value="$2"
  if grep -qE "^${key}=$" "$env_file"; then
    # Use a delimiter unlikely to appear in image refs / passwords
    awk -v k="$key" -v v="$value" '
      $0 == k"=" { print k"="v; next }
      { print }
    ' "$env_file" > "$env_file.tmp" && mv "$env_file.tmp" "$env_file"
  fi
}

# Generate a random secret if `openssl` is available; otherwise fall back to /dev/urandom.
gen_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 24 | tr -d '\n='
  else
    head -c 18 /dev/urandom | base64 | tr -d '\n='
  fi
}

fill_default FLEANS_CORE_IMAGE       "ghcr.io/nightbaker/fleans-api:${version}"
fill_default FLEANS_MANAGEMENT_IMAGE "ghcr.io/nightbaker/fleans-web:${version}"
fill_default FLEANS_MCP_IMAGE        "ghcr.io/nightbaker/fleans-mcp:${version}"
fill_default FLEANS_WORKER_IMAGE     "ghcr.io/nightbaker/fleans-worker:${version}"
fill_default FLEANS_CORE_PORT        "8080"
fill_default FLEANS_MANAGEMENT_PORT  "8080"
fill_default FLEANS_MCP_PORT         "8080"
fill_default CLUSTER_CLUSTER_ID      "fleans"
fill_default CLUSTER_SERVICE_ID      "fleans"
fill_default ORLEANS_REDIS_PASSWORD  "$(gen_secret)"
fill_default POSTGRES_PASSWORD       "$(gen_secret)"

# 2) Host-port mappings — Aspire emits `ports: ["${FLEANS_CORE_PORT}"]` which Compose treats
#    as "random host port → container port". Rewrite to fixed host bindings so the documented
#    `localhost:8080` (Web admin UI) and `localhost:8081` (API) URLs work out of the box.
#    Web is the primary user-facing service and gets 8080; API exposes the REST surface for
#    automation/CI on 8081. MCP stays internal-only by default. Container-side stays
#    parameterised via .env; only the host side is fixed.
sed -i.bak \
  -e 's|^      - "${FLEANS_MANAGEMENT_PORT}"$|      - "8080:${FLEANS_MANAGEMENT_PORT}"|' \
  -e 's|^      - "${FLEANS_CORE_PORT}"$|      - "8081:${FLEANS_CORE_PORT}"|' \
  "$yaml_file"
rm -f "$yaml_file.bak"

# 3) POSTGRES_DB — Aspire's `.AddDatabase("fleans")` produces a connection string that
#    references the "fleans" database, but the compose publisher does NOT emit a
#    POSTGRES_DB environment variable on the postgres service. The official postgres image
#    only creates a database matching POSTGRES_DB on first init, so without this the silos
#    would crash with `database "fleans" does not exist`.
if grep -q '^  postgres:' "$yaml_file" && ! grep -q '^      POSTGRES_DB:' "$yaml_file"; then
  awk '
    /^  postgres:$/ { in_pg = 1 }
    in_pg && /^      POSTGRES_USER:/ {
      print "      POSTGRES_DB: \"fleans\""
      in_pg = 0
    }
    { print }
  ' "$yaml_file" > "$yaml_file.tmp" && mv "$yaml_file.tmp" "$yaml_file"
fi

# Helper: does the named service block already contain a given top-level key?
# Used for idempotency on §4 and §5 so re-running the script (or operators who
# hand-edited the bundle) doesn't get their config clobbered.
service_has_key() {
  local file="$1" svc="$2" key="$3"
  awk -v svc_line="  $svc:" -v key_pat="^    $key:" '
    $0 == svc_line { in_svc = 1; next }
    in_svc && /^  [A-Za-z]/ { in_svc = 0 }
    in_svc && $0 ~ key_pat { found = 1 }
    END { exit found ? 0 : 1 }
  ' "$file"
}

# 4) postgres healthcheck — required so the `depends_on { condition: service_healthy }`
#    we add in §5 has a source of truth. Without this, fleans-* services boot in parallel
#    with postgres and crash their EF Core migration at startup with `Connection refused`
#    when postgres hasn't opened TCP yet. The crash zombies the .NET process at 99% CPU
#    (other DI services prevent clean exit), leaving the container "Up" but unresponsive.
if grep -q '^  postgres:' "$yaml_file" && ! service_has_key "$yaml_file" postgres healthcheck; then
  awk '
    /^  postgres:$/ { in_pg = 1 }
    in_pg && /^    image:/ {
      print
      print "    healthcheck:"
      print "      test: [\"CMD-SHELL\", \"pg_isready -U postgres -d fleans\"]"
      print "      interval: 5s"
      print "      timeout: 3s"
      print "      retries: 30"
      print "      start_period: 5s"
      in_pg = 0
      next
    }
    { print }
  ' "$yaml_file" > "$yaml_file.tmp" && mv "$yaml_file.tmp" "$yaml_file"
fi

# 5) Upgrade `depends_on { postgres: condition: service_started }` (Aspire's default)
#    to `condition: service_healthy` so the §4 healthcheck is actually consulted. Without
#    this, the existing depends_on only waits for postgres's CONTAINER to launch, not for
#    postgres to be accepting connections — the race window is ~10-15s on first-init.
#    Targets a 2-line YAML pattern: "      postgres:\n        condition: \"service_started\"".
awk '
  /^      postgres:$/ { saw_pg = 1; print; next }
  saw_pg && /^        condition: "service_started"$/ {
    print "        condition: \"service_healthy\""
    saw_pg = 0
    next
  }
  { saw_pg = 0; print }
' "$yaml_file" > "$yaml_file.tmp" && mv "$yaml_file.tmp" "$yaml_file"

# 6) restart: on-failure on every fleans-* service — self-heals on transient failures
#    (e.g., postgres restart mid-run). Aspire does NOT emit any restart policy by default,
#    so a crash anywhere downstream of startup is permanent until the operator intervenes.
for svc in fleans-core fleans-management fleans-mcp fleans-worker; do
  if grep -q "^  ${svc}:" "$yaml_file" && ! service_has_key "$yaml_file" "$svc" restart; then
    awk -v svc="$svc" '
      $0 == "  " svc ":" { in_svc = 1 }
      in_svc && /^    image:/ {
        print
        print "    restart: on-failure"
        in_svc = 0
        next
      }
      { print }
    ' "$yaml_file" > "$yaml_file.tmp" && mv "$yaml_file.tmp" "$yaml_file"
  fi
done

echo "Compose bundle post-processed at $bundle_dir (version=$version)."
