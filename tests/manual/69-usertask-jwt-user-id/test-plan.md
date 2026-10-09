# 69 — User-task acting user comes from the JWT

Verifies #793: with JWT auth enabled, `POST /UserTasks/{id}/claim` and `/complete` act as the user in the token's `Authentication:UserIdClaim` claim (default `preferred_username`). A body `UserId` that differs from the token gets `403 Forbidden`. Before #793 the body `UserId` was trusted, so any authenticated caller could claim or complete as somebody else (for example, as the task's `assignee`).

**Automated:** `Fleans.E2E.Tests/Specs/AuthenticationTests.cs` → `UserTaskClaimAndComplete_UseJwtUserId_BodySuppliedUserIdCannotImpersonate` (CI job `e2e-auth`). Run this plan by hand only when you change the IdP setup or `Authentication:UserIdClaim` handling.

## Prerequisites

- Keycloak realm with users `alice` and `bob`, and an API client whose tokens carry `aud=fleans-api`. Easiest: run the stack with `FLEANS_E2E_AUTH=true dotnet run --project Fleans.Aspire` from `src/Fleans/`, which imports `Fleans.Aspire/e2e-auth/fleans-realm.json`. See `tests/manual/30-web-auth/keycloak-dev.md` for a standalone Keycloak.
- Tokens:

```bash
KC=<keycloak base url>
token() { curl -s "$KC/realms/fleans/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=fleans-e2e -d client_secret=fleans-e2e-secret \
  -d username=$1 -d password=$1 -d scope=openid | jq -r .access_token; }
ALICE=$(token alice); BOB=$(token bob)
```

## Steps

### 1. Deploy and start

```bash
curl -k -X POST https://localhost:7140/Definitions/deploy -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' --data-binary @tests/manual/69-usertask-jwt-user-id/assignee-claim.bpmn
WID=$(curl -sk -X POST https://localhost:7140/Execution/start -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -d '{"WorkflowId":"assignee-claim"}' | jq -r .workflowInstanceId)
TASK=$(curl -sk "https://localhost:7140/UserTasks?assignee=alice" -H "Authorization: Bearer $ALICE" \
  | jq -r --arg wid "$WID" '.items[] | select(.workflowInstanceId == $wid) | .activityInstanceId')
```

- [ ] `TASK` is a GUID.

### 2. bob impersonates alice → 403

```bash
curl -sk -i -X POST https://localhost:7140/UserTasks/$TASK/claim -H "Authorization: Bearer $BOB" \
  -H 'Content-Type: application/json' -d '{"UserId":"alice"}'
```

- [ ] `403 Forbidden`, body `{"error":"UserId does not match the authenticated user"}` (no user names in it).
- [ ] Api log has a Warning with EventId 8009.
- [ ] `GET /UserTasks/$TASK` still shows `claimedBy: null`.

### 3. bob as himself → 409

```bash
curl -sk -i -X POST https://localhost:7140/UserTasks/$TASK/claim -H "Authorization: Bearer $BOB" \
  -H 'Content-Type: application/json' -d '{}'
```

- [ ] `409 Conflict` — bob is not the assignee.

### 4. alice without a body UserId → 200

```bash
curl -sk -i -X POST https://localhost:7140/UserTasks/$TASK/claim -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -d '{}'
```

- [ ] `200 OK`; `GET /UserTasks/$TASK` shows `claimedBy: "alice"`.

### 5. Complete

```bash
curl -sk -i -X POST https://localhost:7140/UserTasks/$TASK/complete -H "Authorization: Bearer $BOB" \
  -H 'Content-Type: application/json' -d '{"UserId":"alice"}'
curl -sk -i -X POST https://localhost:7140/UserTasks/$TASK/complete -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -d '{}'
```

- [ ] bob's call returns `403`; alice's returns `200`.
- [ ] `GET /Instances/$WID/state` shows `isCompleted: true`.

### 6. No-auth regression

Restart without `FLEANS_E2E_AUTH` and repeat a claim with `{"UserId":"alice"}` and no token.

- [ ] `200 OK` — the body `UserId` is still trusted when auth is disabled.
- [ ] A claim with `{}` returns `400` `UserId is required`.
