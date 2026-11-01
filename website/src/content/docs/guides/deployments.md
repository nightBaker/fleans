---
title: Deploy approvals and environments
description: Require reviewers to approve a deploy, and promote a version from dev to test to prod.
---

Out of the box a deploy is immediate: whoever clicks **Deploy** in the editor (or calls
`POST /Definitions/deploy`) puts a new version live. Two opt-in settings change that:

- **Deploy approvals** — a cluster can require *N* reviewers to approve each deploy.
- **Environments** — an Admin UI can **promote** a deployed version to another Fleans cluster
  (dev → test → prod), where that cluster's own approval rule applies.

## Deploy approvals

Set the number of approvals per cluster:

| Env var | Default | Meaning |
|---|---|---|
| `Fleans__Deployments__RequiredApprovals` | `0` | `0` deploys immediately. Above `0`, every deploy becomes a **deployment request** that this many reviewers must approve first. |

Set it on **every host of the cluster** — the API, the Admin UI and the MCP server each enforce it —
the same way you set `Authentication__*` on both. With Helm, add it to `extraEnv`:

```yaml
extraEnv:
  - name: Fleans__Deployments__RequiredApprovals
    value: "2"
```

### What changes

- The editor's **Deploy** button opens **Request deployment**: the same checklist, a note for the
  reviewers, and *Review: 0 of 2*. Nothing is deployed yet; the tab stays a draft.
- `POST /Definitions/deploy` and the MCP deploy tool answer **409** — submit a request with
  [`POST /Deployments/requests`](/fleans/reference/api/#deployment-request-endpoints) instead.
- The **Deployments** page lists open requests. Select one to see the requester, the note, the
  approvals so far and the diagram, then **Approve** (optional note) or **Reject** (reason
  required). The requester can **Withdraw** it.
- When the last required approval arrives, the request **deploys itself** as the next version of
  the process. *Deploy disabled* on the request still applies: the version is deployed, but no new
  instances start until someone enables the process on Workflows.

### Who may approve

- an identified user — with [authentication](/fleans/reference/authentication/) on, the signed-in
  user (`preferred_username`); without it, the name the reviewer types on the Deployments page
  (self-declared, as for user tasks over the API);
- **not the requester**;
- each reviewer **once** — two approvals need two different people;
- when **reviewer groups** are set, a member of at least one of them.

| Env var | Default | Meaning |
|---|---|---|
| `Fleans__Deployments__ReviewerGroups__0`, `__1`, … | *(none)* | Groups whose members may approve or reject. Empty: anyone but the requester. |

Groups come from the claim named by `Authentication__GroupsClaim` (default `groups`) — the same
claim user tasks use for `candidateGroups`. To restrict reviewers by **role** instead, set
`Authentication__GroupsClaim=roles` and list role names. Set the claim on the API and the Admin UI
alike. Without authentication, reviewers type their groups next to their name on the Deployments
page (self-declared), and API callers send `UserGroups` in the body.

Like the approval count, the groups are fixed when a request is submitted. The Deployments page
shows them on each request and explains why the buttons are disabled for someone outside them.

A request whose conditions or scripts do not compile is rejected when it is submitted, with the
element and parser message, so reviewers never see a version that could not deploy.

### When a deploy fails

The approvals are saved before the deploy runs. If the deploy itself fails (for example the
database is briefly unavailable), the request stays **Approved** with the error shown, and
**Retry deployment** runs it again without asking the reviewers twice.

The required count is fixed when a request is submitted: raising the setting later does not move
the goal posts of requests already open.

## Environments and promotion

Each Admin UI manages one cluster. To promote versions onward, tell it its own name and the
clusters downstream of it. The chain is configured hop by hop — dev's Admin UI lists `test`,
test's lists `prod`:

| Env var (Admin UI) | Meaning |
|---|---|
| `Fleans__Environment__Name` | This cluster's name, shown in the deploy dialog ("Deploy · dev"). |
| `Fleans__Environment__PromotionTargets__0__Name` | A target's name, e.g. `test`. |
| `Fleans__Environment__PromotionTargets__0__ApiUrl` | The target's REST API base URL. |
| `Fleans__Environment__PromotionTargets__0__TokenUrl` | *Optional.* OAuth2 token endpoint, for a target with authentication on. |
| `Fleans__Environment__PromotionTargets__0__ClientId` | *Optional.* Client-credentials client id. Setting it turns token use on. |
| `Fleans__Environment__PromotionTargets__0__ClientSecret` | *Optional.* Client secret — source it from a Secret. |
| `Fleans__Environment__PromotionTargets__0__Scope` | *Optional.* Scope to request. |

Use `__1__`, `__2__`, … for more targets. The Admin UI checks the list at startup (unique names,
absolute URLs) and refuses to start on a mistake.

With targets configured, each version on the **Workflows** page gets **Promote**. Pick the target,
add a note and promote: the Admin UI reads that version's BPMN from this cluster and submits it to
the target's `POST /Deployments/requests`. The target then follows **its own**
`RequiredApprovals`:

- `0` — it deploys at once, and the dialog shows the new version number;
- above `0` — it opens a request, and the target's reviewers approve it on the target's
  Deployments page.

The note records the source environment, process, version and the user who promoted it. With
client credentials the token's user (the service account) is the requester on the target; without
them, the promoting user's name is.

```yaml
# Helm values of the *dev* release
extraEnv:
  - name: Fleans__Environment__Name
    value: dev
  - name: Fleans__Environment__PromotionTargets__0__Name
    value: test
  - name: Fleans__Environment__PromotionTargets__0__ApiUrl
    value: https://fleans-test.example.com
  - name: Fleans__Environment__PromotionTargets__0__TokenUrl
    value: https://idp.example.com/realms/fleans/protocol/openid-connect/token
  - name: Fleans__Environment__PromotionTargets__0__ClientId
    value: fleans-promoter
  - name: Fleans__Environment__PromotionTargets__0__ClientSecret
    valueFrom:
      secretKeyRef: { name: fleans-promoter, key: client-secret }
```

The Admin UI must be able to reach the target's API over the network, and the target's API must
accept the token (its `Authentication__Audience`).

## Environments board

**Deployments → Environments** shows, for each process deployed here, which version every
promotion target runs and how it compares:

| Cell | Meaning |
|---|---|
| **in sync** | The target runs this cluster's latest diagram. |
| **runs v*K*** | The target runs this cluster's older version *K* — promote to catch it up. |
| **differs** | The target runs a diagram this cluster never deployed (edited there directly). |
| *not deployed* | The target has no version of the process. |
| ***N* pending** | Deployment requests for the process wait for reviewers on the target. |

Version numbers are per cluster — v3 here and v1 on the target can be the same diagram — so the
board compares the **BPMN content** (a SHA-256 of the stored XML; promotion keeps the XML exact).
It reads each target's `GET /Definitions/latest` and `GET /Deployments/requests` with the same
credentials as promotion; a target that can't be reached is marked *unreachable* without hiding
the others. **Promote** on a row opens the promote dialog with the first out-of-sync target
selected. The board lists the first 50 processes; search narrows it down.
