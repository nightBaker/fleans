---
title: Authentication
description: Opt-in JWT bearer authentication for the Fleans REST API.
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

## Testing & troubleshooting

The manual regression test plan for authentication lives in [`tests/manual/28-api-auth/test-plan.md`](https://github.com/nightBaker/fleans/blob/main/tests/manual/28-api-auth/test-plan.md). It verifies:
- API works unauthenticated by default
- Returns `401` when auth is configured and no token is provided
- Accepts valid tokens

**Common errors:**

| Symptom | Likely cause |
|---------|-------------|
| `401` with no `WWW-Authenticate` response header | `Authority` URL is unreachable or the OIDC discovery endpoint returned an error at startup |
| `401` with `invalid_token` in `WWW-Authenticate` | `Audience` mismatch — the token's `aud` claim doesn't match the configured value |
| `401` on `/health` or `/alive` | Not expected — these endpoints are always anonymous; check for a reverse proxy stripping the path |

## Related

- Web admin authorization — tracked separately in [#370](https://github.com/nightBaker/fleans/issues/370).
