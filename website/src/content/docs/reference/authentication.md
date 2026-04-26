---
title: Authentication
description: Opt-in OIDC authentication for the Fleans REST API and Management UI.
sidebar:
  order: 2
---

The Fleans REST API supports **opt-in JWT bearer authentication** via any OIDC-compliant identity provider (Keycloak, Auth0, Microsoft Entra ID, etc.). Authentication is **disabled by default** — when no `Authentication:Authority` is configured, the API runs fully unauthenticated, identical to previous behavior.

## Why opt-in?

Local development and single-tenant deployments often don't need authentication. Production multi-tenant deployments do. By making it configuration-driven, the same binary serves both scenarios without recompilation.

## Quick start

Add the `Authentication` section to your `appsettings.json`:

```json
{
  "Authentication": {
    "Authority": "https://your-idp.example.com/realms/fleans",
    "Audience": "fleans-api",
    "RequireHttpsMetadata": true
  }
}
```

Once `Authority` is set, all `/Workflow/*` endpoints require a valid `Authorization: Bearer <token>` header. Requests without a token receive `401 Unauthorized`.

**Environment variable equivalent** (for Docker Compose or container deployments):

```bash
Authentication__Authority=https://your-idp.example.com/realms/fleans
Authentication__Audience=fleans-api
Authentication__RequireHttpsMetadata=false
```

## Configuration reference

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `Authority` | Yes (to enable auth) | *(absent — auth disabled)* | OIDC issuer URL. When set, all API endpoints require a valid JWT. |
| `Audience` | No | `fleans-api` | Expected `aud` claim in the JWT. |
| `RequireHttpsMetadata` | No | `true` | Set to `false` only for local dev with an HTTP-only IdP (e.g., Keycloak dev mode). |

## Behavior when enabled

- **All `/Workflow/*` endpoints** require a valid `Authorization: Bearer <token>` header. Unauthenticated requests receive `401 Unauthorized`.
- **Health endpoints** (`/health`, `/alive`) remain anonymous — they are exempt so that load balancers and orchestrators can probe without credentials. See [`Fleans.ServiceDefaults/Extensions.cs`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ServiceDefaults/Extensions.cs) for the implementation.
- **Swagger UI** remains accessible in development mode for testing.

## Identity providers

### Keycloak

```bash
# 1. Start Keycloak dev instance
docker run -p 8080:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
  -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  quay.io/keycloak/keycloak:latest start-dev

# 2. Create realm "fleans", client "fleans-api" with client credentials grant

# 3. Configure Fleans
#    appsettings.json:
#    "Authentication": {
#      "Authority": "http://localhost:8080/realms/fleans",
#      "Audience": "fleans-api",
#      "RequireHttpsMetadata": false
#    }

# 4. Obtain a token and call the API
TOKEN=$(curl -s -X POST http://localhost:8080/realms/fleans/protocol/openid-connect/token \
  -d "grant_type=client_credentials" \
  -d "client_id=fleans-api" \
  -d "client_secret=YOUR_SECRET" | jq -r '.access_token')

curl -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"BpmnXml":"..."}'
```

### Other OIDC providers

Configuration is identical — set `Authority` to your provider's OIDC discovery URL and `Audience` to the configured client/API identifier. Provider-specific walkthroughs will be added as contributors submit them.

## Client examples

### curl

```bash
# Obtain a token from your identity provider, then:
TOKEN="<your-jwt-token>"

curl -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"WorkflowId":"my-process"}'
```

### .NET HttpClient

Use a `DelegatingHandler` to attach the bearer token automatically on every outgoing request:

```csharp
public class BearerTokenHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    public BearerTokenHandler(ITokenProvider tokenProvider)
        => _tokenProvider = tokenProvider;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}

// Registration (e.g., in Program.cs):
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddHttpClient<IFleansClient, FleansClient>(client =>
{
    client.BaseAddress = new Uri("https://localhost:7140");
})
.AddHttpMessageHandler<BearerTokenHandler>();
```

## Management UI

The `Fleans.Web` Blazor admin UI supports the same opt-in pattern via OIDC Authorization Code flow with PKCE, persisting the resulting identity in a session cookie. **Disabled by default** — when no `Authentication` section is present, the UI runs unauthenticated, identical to today's behaviour.

### Why a different flow than the API?

Browser users don't hold raw JWTs. The canonical pattern for server-side web apps is OIDC Authorization Code flow with PKCE, which exchanges the authorization code for tokens server-side and persists the identity in an encrypted session cookie. The API uses bearer JWTs because its callers are scripts and services that *do* hold tokens; the UI uses cookies because its callers are humans in browsers.

### Config block (Management UI)

```json
{
  "Authentication": {
    "Authority":             "https://your-idp.example.com/realms/fleans",
    "ClientId":              "fleans-web",
    "ClientSecret":          "<from-IdP>",
    "RequireHttpsMetadata":  true,
    "CookieExpireMinutes":   60,
    "KnownProxies":          [],
    "KnownNetworks":         []
  }
}
```

Auth on iff **both** `Authority` AND `ClientId` are non-empty (single source of truth — same key namespace as the API). The shipped `appsettings.json` carries no `Authentication` block; a documented copy lives at [`src/Fleans/Fleans.Web/appsettings.example.jsonc`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Web/appsettings.example.jsonc) and is not loaded at runtime.

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `Authority` | Yes (to enable auth) | *(absent — auth disabled)* | OIDC issuer URL. |
| `ClientId` | Yes (to enable auth) | *(absent)* | OAuth client id registered with the IdP. Must be a confidential client. |
| `ClientSecret` | Yes when auth on | — | Confidential-client secret. Use `dotnet user-secrets` in dev; Aspire `auth-client-secret` parameter (`secret: true`) or env var `Authentication__ClientSecret` in prod. **Never commit.** |
| `RequireHttpsMetadata` | No | `true` | Set to `false` only for local Keycloak dev mode (HTTP). |
| `CookieExpireMinutes` | No | `60` | Sliding session cookie lifetime. Mirror the IdP's access-token / management-page session lifetime so admin sessions in the UI and tokens used against the API don't drift apart. |
| `KnownProxies` | No | `[]` | Reverse-proxy IP addresses trusted to set `X-Forwarded-For` / `X-Forwarded-Proto`. Empty list = headers ignored. |
| `KnownNetworks` | No | `[]` | Same as above but accepts CIDR ranges (e.g. `10.0.0.0/8`). |

### Aspire wiring

`Fleans.Aspire/Program.cs` declares three optional parameters (always present, defaulting to empty strings) that are forwarded to `Fleans.Web` as env vars:

```csharp
var authAuthority    = builder.AddParameter("auth-authority", () => "");
var authClientId     = builder.AddParameter("auth-client-id", () => "");
var authClientSecret = builder.AddParameter("auth-client-secret", () => "", secret: true);

builder.AddProject<Projects.Fleans_Web>("fleans-management")
    .WithEnvironment("Authentication__Authority",   authAuthority)
    .WithEnvironment("Authentication__ClientId",    authClientId)
    .WithEnvironment("Authentication__ClientSecret", authClientSecret)
    /* ... */;
```

Operators set the parameters at run-time (Aspire CLI prompt, env vars, deployment manifest). When unset, `Fleans.Web` sees empty strings and falls into auth-disabled mode automatically.

### Behaviour when enabled

- **Every page** is wrapped in `<AuthorizeRouteView>`. Unauthenticated requests trigger an OIDC challenge → IdP login → callback to `/signin-oidc` → session cookie issued → bounce to the originally-requested URL (deep links preserved).
- **Orleans Dashboard at `/dashboard`** is gated by an explicit middleware branch — Orleans' dashboard middleware does not honour `[Authorize]`, so the guard fires before `MapOrleansDashboard`.
- **NavMenu** renders a `Signed in as <preferred_username>` chip with a `Sign out` button. The button submits an antiforgery-protected POST to `/Account/Logout` that clears both the cookie and the IdP session.
- **`/health`, `/alive`** stay anonymous (operator probes are unaffected).
- **`/Account/Login?returnUrl=…`** validates `returnUrl` against an inline `IsLocalUrl` predicate (same shape as `IUrlHelper.IsLocalUrl`). Open-redirect attacks (`/\evil.com`, `//evil.com`, absolute URLs, leading whitespace) collapse to `/`; well-formed local paths pass through.

### Multi-instance deployments

When `Fleans.Web` runs as more than one replica, ASP.NET Data Protection keys are persisted to the existing `orleans-redis` Aspire resource so cookies issued by replica A decrypt on replica B. Single-replica deployments use the same path (it's a no-op on key cardinality, not behaviour).

### Reverse proxies

If `Fleans.Web` sits behind a reverse proxy that terminates TLS, populate `KnownProxies` (or `KnownNetworks` for CIDR ranges) so `X-Forwarded-For` / `X-Forwarded-Proto` are honoured when constructing OIDC redirect URIs. Empty defaults are deliberately strict — without an explicit allowlist the framework discards the headers, preventing host-spoofing from untrusted networks.

### Local dev

A copy-paste Keycloak quickstart (Docker run, realm JSON, `dotnet user-secrets` commands, sample user) lives at [`tests/manual/30-web-auth/keycloak-dev.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/30-web-auth/keycloak-dev.md). The full manual test plan (login round-trip, dashboard guard, open-redirect attack table, antiforgery on logout) is at [`tests/manual/30-web-auth/test-plan.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/30-web-auth/test-plan.md).

### Roles (deferred)

This slice authenticates only — every signed-in user has the same access. Role-based policies (`Admin`, `Operator`, `Viewer`) and per-page `[Authorize(Roles=…)]` are deliberately a separate slice, mirroring the same staging used for the API in [#341](https://github.com/nightBaker/fleans/issues/341). The OIDC handler already maps the `roles` claim from the token; the follow-up slice adds policy registration and component-level enforcement.

## Testing & troubleshooting

The manual regression test plans for authentication live at:
- API: [`tests/manual/28-api-auth/test-plan.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/28-api-auth/test-plan.md) — verifies the API works unauthenticated by default, returns `401` when auth is on and no token is provided, and accepts valid tokens.
- Management UI: [`tests/manual/30-web-auth/test-plan.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/30-web-auth/test-plan.md) — verifies anonymous browse is allowed when no `Authentication` section is present, every page (and `/dashboard`) returns 302 → IdP when auth is on, login round-trip preserves deep-link query strings, the open-redirect guard rejects every canonical attack input, and `/Account/Logout` is antiforgery-protected.

**Common errors:**

| Symptom | Likely cause |
|---------|-------------|
| `401` with no `WWW-Authenticate` response header | `Authority` URL is unreachable or the OIDC discovery endpoint returned an error at startup |
| `401` with `invalid_token` in `WWW-Authenticate` | `Audience` mismatch — the token's `aud` claim doesn't match the configured value |
| `401` on `/health` or `/alive` | Not expected — these endpoints are always anonymous; check for a reverse proxy stripping the path |

## Related

- Role-based authorization for both API and Management UI — tracked in [#341](https://github.com/nightBaker/fleans/issues/341).
