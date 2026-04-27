# Management UI Authentication — Manual Test Plan

## Scenario

Verify that opt-in OIDC cookie auth for the `Fleans.Web` Blazor admin UI is configuration-driven: absent config preserves today's open access, while a populated `Authentication` block forces every page (and the Orleans Dashboard) through the configured IdP, surfaces a "Signed in as …" chip with antiforgery-protected logout, and rejects open-redirect attempts on `/Account/Login?returnUrl=…`.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- For **Scenario 1** (no auth): default `appsettings.json` with no `Authentication` section
- For **Scenarios 2–6** (auth enabled): a running OIDC provider — see `keycloak-dev.md` for a copy-paste local setup
- Web UI reachable at `https://localhost:7124`

## Scenario 1 — Auth disabled (default)

Confirms the opt-in default is unchanged.

1. Ensure `appsettings.json` contains no `Authentication` section, and no `Authentication__Authority` / `Authentication__ClientId` env vars or user-secrets are set on `Fleans.Web`.
2. Launch Aspire and open `https://localhost:7124/workflows`.
3. **Expect:** the page renders directly (HTTP 200), no redirect to any IdP. The NavMenu shows Workflows + Editor only — no "Signed in as …" chip.
4. Visit `https://localhost:7124/dashboard` → Orleans Dashboard renders without a redirect.
5. Visit `https://localhost:7124/health` and `/alive` → both return 200 (anonymous health probes still work).

Checklist:
- [ ] `/workflows` returns 200 with no redirect
- [ ] `/dashboard` renders without auth
- [ ] `/health` and `/alive` return 200
- [ ] NavMenu has no user/logout chip

## Scenario 2 — Auth enabled, successful login

Confirms the OIDC handshake round-trip and post-login destination preservation.

1. Configure `Fleans.Web` with the secrets listed in `keycloak-dev.md` (user-secrets or env vars).
2. Launch Aspire and open `https://localhost:7124/workflows?filter=active` in a fresh incognito window.
3. **Expect:** browser is redirected to `http://localhost:8081/realms/fleans/protocol/openid-connect/auth?...` (the Keycloak login form).
4. Sign in as `alice` / `alice`.
5. **Expect:** browser bounces back through `/signin-oidc` and lands on `/workflows?filter=active` (deep-link preserved).
6. **Expect:** the NavMenu's user chip reads `Signed in as alice` and includes a `Sign out` button.
7. Open `https://localhost:7124/dashboard` in the same session → Orleans Dashboard loads without an additional login prompt.

Checklist:
- [ ] Anonymous request to `/workflows?filter=active` returns 302 → IdP
- [ ] Successful login lands on `/workflows?filter=active` (query string intact)
- [ ] NavMenu shows `Signed in as alice`
- [ ] `/dashboard` accessible after login

## Scenario 3 — Orleans Dashboard guard

Confirms the dashboard requires a session even though the underlying middleware ignores `[Authorize]`.

1. With auth enabled, open a fresh incognito window and visit `https://localhost:7124/dashboard`.
2. **Expect:** HTTP 302 to the IdP login page (NOT 200 with cluster data).
3. Log in as `alice` and confirm the dashboard renders.

Checklist:
- [ ] Anonymous `/dashboard` request → 302 to IdP
- [ ] `/dashboard` renders only after login

## Scenario 4 — Open-redirect guard on `/Account/Login`

Confirms the D6 `IsLocalUrl` predicate rejects every canonical attack input and accepts only well-formed local paths.

For each row, request the URL anonymously and confirm the post-auth `RedirectUri` field of the issued `Set-Cookie` / `Location` header is `/` (or another local path), never the attacker target.

| `?returnUrl=` value | How to send it             | Expected `safe` |
|---------------------|----------------------------|-----------------|
| (omitted)           | `/Account/Login`           | `/`             |
| (empty)             | `/Account/Login?returnUrl=`| `/`             |
| `/\evil.com`        | `/Account/Login?returnUrl=/%5Cevil.com` | `/`  |
| `//evil.com`        | `/Account/Login?returnUrl=//evil.com`   | `/`  |
| `https://evil.com`  | `/Account/Login?returnUrl=https://evil.com` | `/` |
| ` /workflows`       | `/Account/Login?returnUrl=%20/workflows` | `/`   |
| `/workflows?filter=active` | `/Account/Login?returnUrl=%2Fworkflows%3Ffilter%3Dactive` | `/workflows?filter=active` |

Checklist:
- [ ] Every attack-string row redirects to `/` post-auth, not the attacker host
- [ ] The legitimate deep-link row redirects to `/workflows?filter=active`

## Scenario 5 — Logout via antiforgery-protected POST

Confirms the bare-POST logout is rejected and the form-bound POST signs out cleanly.

1. Logged in as `alice`, open browser devtools → Network.
2. Run from the URL bar / shell: `curl -X POST https://localhost:7124/Account/Logout -k --cookie "<session cookie>"`.
3. **Expect:** HTTP 400 (antiforgery rejection) — the bare POST has no token.
4. In the UI, click the `Sign out` button in the NavMenu.
5. **Expect:** HTTP 302 → IdP end-session endpoint → callback → `/`. Both the auth cookie and the OIDC session are cleared (a subsequent `/workflows` request re-challenges).

Checklist:
- [ ] Bare `POST /Account/Logout` returns 400 (antiforgery missing)
- [ ] Form-bound `Sign out` button signs out and clears both cookie and IdP session
- [ ] Post-logout `/workflows` request redirects back to IdP login

## Scenario 6 — Health endpoints stay anonymous

Confirms operator/orchestrator probes are unaffected by auth.

1. With auth enabled and no session cookie, request `https://localhost:7124/health` and `/alive`.
2. **Expect:** both return HTTP 200 with no redirect.

Checklist:
- [ ] `/health` returns 200 even when auth is on
- [ ] `/alive` returns 200 even when auth is on

## Known limitation (not a defect)

When the cookie expires while a Blazor circuit is still open, the framework's "Could not reconnect" overlay appears (the SignalR `/_blazor` endpoint cannot redirect). Reloading the page triggers the normal redirect-to-IdP flow. A future slice may add a `CircuitHandler` that surfaces a re-auth modal proactively; out of scope here.
