# MCP Keycloak Authentication — Manual Test Plan

## Scenario

Verify that the Model Context Protocol server (`Fleans.Mcp`, port `5200`) supports
opt-in JWT bearer authentication that mirrors the existing `Fleans.Api` pattern.

When `Authentication:Authority` is absent, the MCP endpoint serves anonymously
(default dev behaviour). When it is configured, every MCP request must carry a
valid `Authorization: Bearer <token>` whose `aud` claim includes `fleans-mcp`.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- For **Scenario A** (no auth): no `auth-authority` Aspire parameter is set.
- For **Scenario B** (auth enabled): a running OIDC provider (e.g., Keycloak)
  with a realm and a client configured to mint tokens carrying
  `aud: "fleans-mcp"`.

---

## Scenario A — Authentication disabled (default)

Verify the MCP endpoint behaves exactly as before when no
`Authentication:Authority` is configured.

### Steps

1. Start the Aspire stack with default configuration.

2. **Health endpoint is anonymous:**

   ```bash
   curl -i http://localhost:5200/alive
   ```

   Expected: `200`.

3. **MCP endpoint accepts unauthenticated requests:**

   ```bash
   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   ```

   Expected: `200` plus a JSON-RPC response listing the four tools
   (`DeployWorkflow`, `ListDefinitions`, `GetInstanceState`, `ListInstances`).
   No `401`.

### Expected outcomes

- [ ] `GET /alive` returns `200`.
- [ ] `POST /mcp` `tools/list` returns `200` and the four-tool list.
- [ ] No authentication/authorization middleware errors in logs.

---

## Scenario B — Authentication enabled

Verify the MCP endpoint enforces JWT bearer tokens with `aud: "fleans-mcp"`
when `Authentication:Authority` is configured.

### Prerequisites for Scenario B

Start a Keycloak dev instance (reuse the one from `28-api-auth/` or
`30-web-auth/` if already running):

```bash
docker run -p 8080:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
  -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  quay.io/keycloak/keycloak:latest start-dev
```

1. Realm: `fleans` (reuse the existing one if present).
2. Client: create `fleans-mcp` with client authentication enabled
   (`client_credentials` grant), note the client secret.
   Alternative: add an **Audience** protocol mapper on the existing
   `fleans-api` client so its tokens also carry `aud: "fleans-mcp"`.
3. Pass the realm URL to Aspire:

   ```bash
   dotnet run --project Fleans.Aspire -- \
     --auth-authority http://localhost:8080/realms/fleans \
     --auth-client-id fleans-mcp
   ```

   Or set `Authentication__Authority` / `Authentication__Audience` /
   `Authentication__RequireHttpsMetadata=false` as env vars on the
   `fleans-mcp` resource directly (e.g. via `src/Fleans/Fleans.Mcp/appsettings.json`
   uncommented).

### Steps

1. Start the Aspire stack with the auth configuration above and confirm the
   `fleans-mcp` log line `Now listening on: http://localhost:5200`.

2. **Health endpoint stays anonymous:**

   ```bash
   curl -i http://localhost:5200/alive
   ```

   Expected: `200`.

3. **MCP endpoint rejects unauthenticated requests:**

   ```bash
   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   ```

   Expected: `401 Unauthorized`, plus a `WWW-Authenticate: Bearer …` response
   header.

4. **MCP endpoint rejects tokens with the wrong audience:**

   Obtain a token whose `aud` does **not** include `fleans-mcp` (e.g. a token
   minted for `fleans-api` alone), then:

   ```bash
   WRONG_TOKEN="<token-without-fleans-mcp-audience>"
   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -H "Authorization: Bearer $WRONG_TOKEN" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   ```

   Expected: `401 Unauthorized` with `invalid_token` (audience mismatch) in the
   `WWW-Authenticate` header.

5. **MCP endpoint accepts a valid token:**

   ```bash
   TOKEN=$(curl -s -X POST http://localhost:8080/realms/fleans/protocol/openid-connect/token \
     -d "grant_type=client_credentials" \
     -d "client_id=fleans-mcp" \
     -d "client_secret=YOUR_SECRET" | jq -r '.access_token')

   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   ```

   Expected: `200` and the JSON-RPC response listing the four tools.

6. **MCP tool call (`ListDefinitions`) with valid token:**

   ```bash
   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -H "Authorization: Bearer $TOKEN" \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ListDefinitions","arguments":{}}}'
   ```

   Expected: `200` and a JSON response carrying the (possibly empty) paginated
   definition list — confirms the bearer token flows through to Orleans grain
   calls.

7. **Invalid-format token is rejected:**

   ```bash
   curl -i -X POST http://localhost:5200/mcp \
     -H "Content-Type: application/json" \
     -H "Accept: application/json, text/event-stream" \
     -H "Authorization: Bearer not-a-real-jwt" \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   ```

   Expected: `401 Unauthorized`.

### Expected outcomes

- [ ] `GET /alive` returns `200` without a token.
- [ ] `POST /mcp` without token returns `401`.
- [ ] `POST /mcp` with a token missing `aud: "fleans-mcp"` returns `401`.
- [ ] `POST /mcp` `tools/list` with a valid token returns `200` and lists four tools.
- [ ] `POST /mcp` `tools/call` for `ListDefinitions` with a valid token returns `200`.
- [ ] `POST /mcp` with a malformed bearer string returns `401`.
- [ ] Disabling the `Authentication__Authority` env var on the `fleans-mcp` resource
      restores anonymous Scenario-A behaviour after a restart.
