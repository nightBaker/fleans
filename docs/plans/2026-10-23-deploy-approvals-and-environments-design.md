# Deploy approvals and promotion between environments (#900)

Follow-up from #899. The Orbit deploy board shows **dev / test / prod** targets and
**multi-reviewer approval** ("Review: 1 of 2 — prod needs 2"). #899 shipped the board
against what existed: deploy to *this* cluster, and *Deploy disabled* as a stand-in review
step. This design adds the two missing concepts with the fewest moving parts.

## 1. Approvals

### Policy

One setting per Fleans cluster: `Fleans:Deployments:RequiredApprovals` (int, default `0`).

- `0` — today's behaviour: a deploy is immediate.
- `N > 0` — a deploy becomes a **deployment request** that `N` distinct reviewers must
  approve before the version is deployed. The direct deploy paths (`POST /Definitions/deploy`,
  the MCP deploy tool, the editor's Deploy button) refuse with
  `DeploymentApprovalRequiredException` (HTTP 409), so approvals can't be bypassed through a
  public surface.

The setting is read by `WorkflowCommandService` (Api, Web and Mcp each run one), exactly like
`Authentication:*` is configured per host under one section name. Set it on every host of
a cluster (Helm: `extraEnv`; compose: the shared env block). The *required* count is captured
into the request when it is submitted, so changing the setting later does not move the goal
posts of requests already in flight.

*Why not in the silo only?* The Admin UI is an Orleans client and must know the policy to
render the right dialog; a grain round-trip for a constant adds a call path without adding
safety — anyone with Orleans client access can already call `IProcessDefinitionGrain` directly.

### Who may approve

Three rules, enforced by the domain (`DeploymentRequestState`):

1. The reviewer is identified (non-empty user id).
2. The reviewer is **not the requester** (four-eyes).
3. Each reviewer counts **once** (distinct user ids, ordinal).

With authentication on, the id is the signed-in user (`preferred_username` in the Admin UI,
`IUserIdResolver` in the API — a body `UserId` that differs from the token is rejected).
With authentication off, the id is self-declared (the Admin UI asks for a name, the API takes
the body `UserId`) — the same trust model as user tasks. A group/role restriction for
reviewers is a possible follow-up, not built now.

### Lifecycle

```
           approve (count < N)
          ┌──────────┐
          ▼          │
Submit ─► Pending ───┴─► Approved ─► Deployed
          │   │          │  ▲ retry
          │   │          └──┘ (deploy failed, error kept)
          │   └─ reject (reviewer, reason) ─► Rejected
          └───── withdraw (requester) ─────► Withdrawn
```

- `Submit` with `RequiredApprovals = 0` goes straight to `Approved` and deploys — the same
  code path promotion uses against a cluster that needs no approvals.
- `Approved` is persisted **before** the deploy call; the deploy outcome is persisted after.
  A failed deploy leaves the request `Approved` with `DeployError`, and *Retry deployment*
  runs it again. Approvals are never lost to a transient storage error.
- `Withdraw` is allowed while `Pending` or `Approved` (not yet deployed).

### Storage

`DeploymentRequestGrain` (`IGrainWithGuidKey`, `[CorePlacement]`) with
`IPersistentState<DeploymentRequestState>` → table `DeploymentRequests` via
`EfCoreDeploymentRequestGrainStorage` (same shape as `EfCoreUserTaskGrainStorage`: one row
per grain, ETag check). Approvals are a JSON column. The Admin UI and API list requests from
the same table through `IWorkflowQueryService.GetDeploymentRequests` (BPMN XML excluded from
the list). Postgres gets the `AddDeploymentRequests` migration; SQLite picks the table up
from `EnsureCreated` (pre-1.0: delete a stale dev DB).

The grain parses the stored BPMN XML when it deploys, so `IBpmnConverter` moves from
`Fleans.Infrastructure` to `Fleans.Application` (port in Application, adapter in
Infrastructure — the `IConditionExpressionEvaluator` precedent).

## 2. Environments and promotion

An Admin UI stays an Orleans client of **one** cluster. Other environments are reached over
their **REST API** — the only surface a remote cluster offers. This is the one place the
Admin UI makes HTTP calls; it calls *other* clusters, never its own.

```json
"Fleans": {
  "Environment": {
    "Name": "dev",
    "PromotionTargets": [
      { "Name": "test", "ApiUrl": "https://fleans-test.example.com",
        "TokenUrl": "https://idp/realms/fleans/protocol/openid-connect/token",
        "ClientId": "fleans-promoter", "ClientSecret": "…", "Scope": "" }
    ]
  }
}
```

- `Name` labels this cluster in the deploy dialog ("Deploy · dev").
- `PromotionTargets` are the environments a version can be promoted to *from here*. The
  chain dev → test → prod is configured hop by hop: dev's Admin UI lists `test`, test's lists
  `prod`.
- *Promote* (Workflows page, per version) reads that version's BPMN XML from this cluster and
  `POST`s it to the target's `/Deployments/requests`. The target applies **its own** policy: it
  deploys at once (`RequiredApprovals = 0`) or opens a request its reviewers approve in the
  target's Admin UI.
- Auth to the target: optional OAuth2 client credentials (`TokenUrl` + `ClientId` +
  `ClientSecret`). The token's user is then the requester on the target; the promoting user
  and the source version are recorded in the request comment. Without credentials the
  promoting user's name is sent as `RequestedBy` (target with auth off).

## Surfaces

| Surface | Added |
|---|---|
| REST | `DeploymentsController`: `POST /Deployments/requests`, `GET /Deployments/requests[?status=]`, `GET /Deployments/requests/{id}`, `POST …/{id}/approve`, `…/reject`, `…/withdraw`, `…/retry` |
| Admin UI | Editor deploy dialog becomes *Request deployment* when approvals are required; new **Deployments** page (review queue, diagram preview, approve/reject/withdraw/retry); *Promote* on Workflows |
| Config | `Fleans:Deployments:RequiredApprovals`; `Fleans:Environment:Name`, `Fleans:Environment:PromotionTargets` (Admin UI) |

## Not built (follow-ups if asked)

- Reviewer group/role restriction.
- Reading version state *from* remote environments (a cross-environment board).
- Auto-promotion on approval.
