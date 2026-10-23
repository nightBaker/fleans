# 74 — Deploy approvals and promotion between environments

**Feature:** #900. `Fleans:Deployments:RequiredApprovals` turns a deploy into a deployment request that
distinct reviewers (never the requester) approve on the **Deployments** page; the request then deploys
itself. `Fleans:Environment:PromotionTargets` adds **Promote** to the Workflows page, which submits a
version to another cluster's `POST /Deployments/requests`.

**Automated coverage (not repeated here):** `DeploymentRequestStateTests` (every transition rule:
four-eyes, distinct reviewers, reject needs a reason, withdraw by requester only, deploy failure keeps
the request approved), `DeploymentRequestGrainTests` (deploy at once with 0 required, deploy only after
the last approval, self-approval refused, reject, deploy disabled, invalid BPMN, failed deploy + retry,
listing and reactivation), `WorkflowCommandServiceTests` (direct deploy refused when approvals are
required), `DeploymentsControllerTests` (JWT / body identity, 400/403/404, `/Definitions/deploy` → 409),
`EfCoreDeploymentRequestGrainStorageTests` (SQLite + Postgres round trip, ETag). Everything below is
Admin UI interaction and stays manual.

## Prerequisites

- Aspire stack (from `src/Fleans/`), authentication off.
- Before starting it, add to **both** `Fleans.Api/appsettings.Development.json` and
  `Fleans.Web/appsettings.Development.json` (revert afterwards):

  ```json
  "Fleans": { "Deployments": { "RequiredApprovals": 2 } }
  ```

  and to `Fleans.Web/appsettings.Development.json` only, with the `api` resource's https URL from the
  Aspire dashboard (promoting to the same cluster exercises the whole HTTP path):

  ```json
  "Fleans": { "Environment": { "Name": "dev", "PromotionTargets": [ { "Name": "self", "ApiUrl": "https://localhost:7140" } ] } }
  ```

- Delete a stale dev SQLite file first (`$TMPDIR/fleans-dev.db`) so the `DeploymentRequests` table exists.
- Fixture: `approval-sample.bpmn` (this folder).

## Scenarios

### 1. Request a deployment from the editor

1. Editor → open `approval-sample.bpmn` → **Deploy**. **Expected:** the dialog label reads
   `Deploy · dev · needs 2 approvals`; the checklist ends with *Review: 0 of 2*; a **Your name
   (requester)** field and a *What changed?* note are shown; the button reads **Request deployment**
   and is disabled until a name is entered.
2. Enter `alice`, a note, click **Request deployment**. **Expected:** *Deployment of approval-sample
   requested*, *Review: 0 of 2*. The tab stays a draft. Workflows shows no `approval-sample` yet.
3. **Open review**. **Expected:** `/deployments/{id}` with the request selected: status *Pending*,
   *Requested by alice*, the note, *Review: 0 of 2*, and the diagram rendered.

### 2. Review rules on the Deployments page

1. In *Reviewing as* type `alice`. **Expected:** Approve/Reject disabled, *You requested this
   deployment, so someone else reviews it.*; **Withdraw** is shown.
2. Change to `bob`, **Approve**. **Expected:** *Approved — 1 of 2*; review line lists `bob`; still
   Pending; still nothing on Workflows. The hint now says bob has approved and another reviewer is needed.
3. Change to `carol`, **Reject** without a note. **Expected:** Reject is disabled until a note is typed.
4. As `carol` with a note, **Approve** instead. **Expected:** *Approved — approval-sample deployed as v1*;
   status *Deployed* with a link to v1's instances; Workflows lists `approval-sample` v1, enabled.

### 3. Reject and withdraw

1. Request another deployment of the same diagram as `alice`. As `bob`, type a note and **Reject**.
   **Expected:** *Rejected by bob: <note>*; no v2 on Workflows; the request leaves the *Open* filter and
   shows under *All*.
2. Request again as `alice`, then as `alice` click **Withdraw**. **Expected:** *Withdrawn by alice*.

### 4. Direct deploy is closed

1. `curl -k -X POST https://localhost:7140/Definitions/deploy -H 'Content-Type: application/json'
   -d "{\"BpmnXml\": $(jq -Rs . < approval-sample.bpmn)}"`. **Expected:** `409` with a message pointing
   to `/Deployments/requests`.

### 5. Promote to another environment

1. Workflows → `approval-sample` v1 → **Promote**. **Expected:** dialog *Promote · from dev* with a
   `self` target button selected, name field, note, *Deploy disabled on the target*.
2. Enter `dave`, **Promote to self**. **Expected:** *Requested on self*, *Review: 0 of 2* (the target
   needs 2 approvals). Deployments lists a new request by `dave` whose note starts with
   *Promoted from dev approval-sample v1 by dave.*
3. Set `RequiredApprovals` to `0` on both hosts, restart, promote again. **Expected:** *Deployed to self
   as v2*; Deployments page policy line reads *dev deploys immediately — no approvals required*.
4. Set a target `ApiUrl` to `not-a-url` and restart. **Expected:** the Web host fails at startup with
   *needs a Name and an absolute ApiUrl*.

### 6. No approvals configured (default)

1. Remove the settings, restart. **Expected:** the editor's **Deploy** deploys at once as before
   (`Deploy · dev` label, no review line); Workflows shows no **Promote** button without targets; the
   **Deployments** nav entry is present and its policy line says the cluster deploys immediately.
