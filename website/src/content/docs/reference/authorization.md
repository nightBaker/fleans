---
title: Authorization (roles)
description: The three Fleans roles, how they are read from the token, and which API endpoints and Admin UI pages each role may use.
sidebar:
  order: 2
---

When [authentication](/fleans/reference/authentication/) is on, every caller also needs a **role**.
Fleans has three, and each includes the ones below it:

| Role | What it is for | Includes |
|------|----------------|----------|
| `admin` | Deploy, disable and enable definitions; migrate and purge instances; approve deploy requests; edit custom-task settings; promote to other environments | `operator`, `user` |
| `operator` | Start and inspect instances; send messages and signals; complete or fail activities; run external-task workers | `user` |
| `user` | The tasklist: list, claim, complete, fail and cancel user tasks | — |

A signed-in caller with none of these roles gets `403 Forbidden` from every API endpoint and an
*Access denied* page in the Admin UI. Anonymous API calls still get `401 Unauthorized`.

**With authentication off, roles are not checked.** Every endpoint and page stays open, as before.

## Where roles come from

Fleans reads roles from one claim of the token, `Authentication:RoleClaim`, default `roles`. Each role
is a separate value of that claim (a JSON array in the token). Role names are case-sensitive: `Admin`
is not `admin`. Set the same claim name on the API (`Fleans.Api`) and the Admin UI (`Fleans.Web`).

```json
{
  "Authentication": {
    "Authority": "https://your-idp.example.com/realms/fleans",
    "RoleClaim": "roles"
  }
}
```

Environment variable: `Authentication__RoleClaim=roles`.

### Identity providers

- **Keycloak** puts realm roles under `realm_access.roles`, which Fleans does not read. Create realm
  roles `admin`, `operator` and `user`, then add a **User Realm Role** mapper to the `fleans-api` and
  `fleans-web` clients with *Token Claim Name* `roles`, *Multivalued* on, and *Add to ID token*,
  *access token* and *userinfo* on.
- **Microsoft Entra ID** sends app roles in the `roles` claim. Define app roles with the values
  `admin`, `operator` and `user` on the app registration and assign them to users, groups or service
  principals.
- **Auth0** needs an Action that copies the user's roles into a claim. If the claim name is
  namespaced (for example `https://fleans/roles`), set `Authentication:RoleClaim` to it.

Service accounts need a role too. A CI pipeline that deploys needs `admin`. An
[external-task worker](/fleans/guides/external-workers/) (`Fleans__ExternalTaskWorker__AccessToken`)
needs `operator`. Another environment's Admin UI that [promotes](/fleans/guides/deployments/#environments-and-promotion)
versions here calls `POST /Deployments/requests`, so its client-credentials identity needs `admin`.

## API endpoints

`/health` and `/alive` stay anonymous.

| Endpoint | `user` | `operator` | `admin` |
|----------|:------:|:----------:|:-------:|
| `GET /UserTasks`, `GET /UserTasks/{id}` | ✓ | ✓ | ✓ |
| `POST /UserTasks/{id}/claim`, `/unclaim`, `/complete`, `/fail`, `/cancel` | ✓ | ✓ | ✓ |
| `POST /Execution/start`, `/message`, `/signal`, `/evaluate-conditions` | | ✓ | ✓ |
| `POST /Execution/complete-activity`, `/fail-activity` | | ✓ | ✓ |
| `GET /Execution/{instanceId}/activities/{activityInstanceId}/variables` | | ✓ | ✓ |
| `GET /Instances/{id}/state` | | ✓ | ✓ |
| `GET /Definitions`, `/Definitions/latest`, `/Definitions/{key}/instances`, `/Definitions/{key}/{version}/instances`, `/Definitions/{key}/versions/{from}/migration-preview` | | ✓ | ✓ |
| `GET /custom-tasks`, `GET /custom-tasks/{taskType}` | | ✓ | ✓ |
| `GET /Deployments/requests`, `/Deployments/requests/{id}`, `/Deployments/requests/{id}/bpmn` | | ✓ | ✓ |
| External-task worker gRPC service (`Subscribe`, `CompleteTask`, `FailTask`, …) | | ✓ | ✓ |
| `POST /Definitions/deploy`, `/disable`, `/enable` | | | ✓ |
| `POST /Instances/purge`, `/Instances/{id}/migrate`, `/Instances/migrate` | | | ✓ |
| `PUT /custom-tasks/{taskType}/allowed-processes` | | | ✓ |
| `POST /Deployments/requests` (submit), `/{id}/approve`, `/{id}/reject`, `/{id}/withdraw`, `/{id}/retry` | | | ✓ |

Submitting a deployment request is `admin` because, when `Fleans__Deployments__RequiredApprovals` is
`0`, the request deploys at once.

### Roles and the existing user checks

Roles only decide whether a caller may use an endpoint at all. The checks after that are unchanged:

- **User tasks.** A `user` may call `claim`, but the claim still succeeds only if the caller is the
  task's assignee, one of its candidate users, or in one of its candidate groups.
- **Deploy approvals.** An `admin` may call `approve`, but `Fleans__Deployments__ReviewerGroups` and
  the "not the requester" rule still apply.

## Admin UI pages

The side rail shows only the sections the signed-in user may open. Opening another page directly
shows *Access denied* without going back to the identity provider.

| Page | Needs |
|------|-------|
| Settings | signed in (any role or none) |
| Workflows, process instances, an instance's page, Deployments, Events, Knowledge | `operator` |
| Editor, Custom Tasks (`/admin/custom-tasks`), Environments board (`/deployments/environments`) | `admin` |

Buttons for admin-only actions on operator pages are hidden from operators: **New** and **Edit**,
**Migrate**, **Promote**, **Disable** / **Enable** on Workflows; **Migrate** on an instance page;
**Approve**, **Reject** and **Retry deployment** on Deployments.

There is no tasklist page in the Admin UI yet, so a user with only the `user` role sees just Settings.
Use the REST API for user tasks.

:::caution[The Admin UI calls the cluster directly]
The Admin UI talks to Orleans grains, not to the REST API. Its role checks protect people who use the
UI. They do not protect the cluster from anything else that can reach the Orleans gateway. Keep the
silos' gateway port on a private network. Checking roles inside the cluster itself is not part of
this release.
:::

The MCP server (`Fleans.Mcp`) still only checks that a token is valid. It does not check roles yet.

## Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| `403` on every endpoint with a valid token | The token has no `admin`, `operator` or `user` value in `Authentication:RoleClaim`. Decode the token and check the claim's name and values. |
| `403` only on deploy, disable, migrate or purge | The caller is an `operator`. These need `admin`. |
| Admin UI shows *Access denied* after sign-in | The ID token or UserInfo has no role. With Keycloak, turn on *Add to ID token* or *Add to userinfo* on the roles mapper. |
| Roles are in the token but still `403` | The values differ in case (`Admin`), or the roles are nested, like Keycloak's `realm_access.roles`. Fleans reads only one top-level claim. |
