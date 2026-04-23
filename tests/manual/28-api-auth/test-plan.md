# API JWT Authentication — Manual Test Plan

## Scenario

Verify that JWT bearer authentication is opt-in and works correctly when configured.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- For **Scenario A** (no auth): default `appsettings.json` with no `Authentication` section
- For **Scenario B** (auth enabled): a running OIDC provider (e.g., Keycloak) with a realm/client configured for `fleans-api`

---

## Scenario A — Authentication disabled (default)

Verify the API works exactly as before when no `Authentication:Authority` is configured.

### Steps

1. Start the Aspire stack with default configuration (no `Authentication` section in appsettings).

2. **Health endpoints are accessible:**
   ```bash
   curl -k https://localhost:7140/health
   curl -k https://localhost:7140/alive
   ```

3. **API endpoints work without a token:**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"nonexistent"}'
   ```
   Expected: returns a response (error about missing workflow is fine — the point is no 401).

### Expected outcomes

- [ ] `GET /health` returns 200 Healthy
- [ ] `GET /alive` returns 200 Healthy
- [ ] `POST /Workflow/start` does NOT return 401 Unauthorized
- [ ] No authentication/authorization middleware errors in logs

---

## Scenario B — Authentication enabled

Verify the API enforces JWT bearer tokens when `Authentication:Authority` is configured.

### Prerequisites for Scenario B

Set up an OIDC provider (e.g., Keycloak dev instance):

```bash
docker run -p 8080:8080 -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin quay.io/keycloak/keycloak:latest start-dev
```

1. Create a realm (e.g., `fleans`)
2. Create a client `fleans-api` with client authentication enabled
3. Note the client secret

Configure `appsettings.json` (or environment variables):

```json
{
  "Authentication": {
    "Authority": "http://localhost:8080/realms/fleans",
    "Audience": "fleans-api",
    "RequireHttpsMetadata": false
  }
}
```

### Steps

1. Start the Aspire stack with the authentication configuration above.

2. **Health endpoints still accessible without a token:**
   ```bash
   curl -k https://localhost:7140/health
   curl -k https://localhost:7140/alive
   ```

3. **API endpoints reject unauthenticated requests:**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -d '{"WorkflowId":"test"}'
   ```
   Expected: 401 Unauthorized.

4. **Obtain a token and make an authenticated request:**
   ```bash
   TOKEN=$(curl -s -X POST http://localhost:8080/realms/fleans/protocol/openid-connect/token \
     -d "grant_type=client_credentials" \
     -d "client_id=fleans-api" \
     -d "client_secret=YOUR_SECRET" | jq -r '.access_token')

   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"WorkflowId":"test"}'
   ```
   Expected: request is processed (no 401).

5. **Expired/invalid token is rejected:**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/start \
     -H "Content-Type: application/json" \
     -H "Authorization: Bearer invalid-token-here" \
     -d '{"WorkflowId":"test"}'
   ```
   Expected: 401 Unauthorized.

### Expected outcomes

- [ ] `GET /health` returns 200 without a token
- [ ] `GET /alive` returns 200 without a token
- [ ] `POST /Workflow/start` without token returns 401
- [ ] `POST /Workflow/start` with valid token does NOT return 401
- [ ] `POST /Workflow/start` with invalid token returns 401
- [ ] `POST /Workflow/deploy` without token returns 401
- [ ] `POST /Workflow/message` without token returns 401
- [ ] `POST /Workflow/signal` without token returns 401
