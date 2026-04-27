# Local Keycloak Quickstart

A copy-paste recipe for running a Keycloak dev instance pre-configured with the `fleans` realm + `fleans-web` client used by the manual test plan.

## Run Keycloak with the realm imported

Run from the repository root so the `-v` mount path resolves:

```bash
docker run -d --name fleans-keycloak \
  -p 8081:8080 \
  -e KEYCLOAK_ADMIN=admin -e KEYCLOAK_ADMIN_PASSWORD=admin \
  -v "$(pwd)/tests/manual/30-web-auth/keycloak-fleans-realm.json:/opt/keycloak/data/import/realm.json:ro" \
  quay.io/keycloak/keycloak:latest start-dev --import-realm
```

Verify Keycloak is up: `curl -fsS http://localhost:8081/realms/fleans/.well-known/openid-configuration`.

## Configure Fleans.Web user-secrets

```bash
cd src/Fleans/Fleans.Web
dotnet user-secrets set "Authentication:Authority"            "http://localhost:8081/realms/fleans"
dotnet user-secrets set "Authentication:ClientId"             "fleans-web"
dotnet user-secrets set "Authentication:ClientSecret"         "fleans-web-dev-secret"
dotnet user-secrets set "Authentication:RequireHttpsMetadata" "false"
```

(`RequireHttpsMetadata=false` is needed because the dev Keycloak serves HTTP only.)

## Test the round-trip

```bash
cd src/Fleans
dotnet run --project Fleans.Aspire
```

Open `https://localhost:7124` → expect a 302 to `http://localhost:8081/realms/fleans/protocol/openid-connect/auth?...`. Log in as `alice` / `alice`. After the IdP redirects back through `/signin-oidc`, the NavMenu chip reads `Signed in as alice`.

## Sample user

The realm JSON ships one user: `alice` with password `alice`. Add more via the Keycloak admin UI at `http://localhost:8081/admin` (admin / admin).

## Cleanup

```bash
docker rm -f fleans-keycloak
```

## Why a confidential client?

Server-side Blazor uses the OIDC Authorization Code flow with PKCE and a client secret — it is the canonical pattern for server-side web apps. The secret is read from configuration (user-secrets in dev, Aspire `auth-client-secret` parameter in prod) and never committed to source control. Public clients are reserved for browser SPAs and mobile apps; they are wrong for a server-side admin UI.
