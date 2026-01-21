## BPMN upload (Web UI) — Camunda-like deployments & versioning

### Status

Draft validated via discussion on 2026-01-14.

### Goal

Allow users to upload a BPMN file from the Blazor admin UI and register it in the running Orleans cluster. Uploading the same BPMN process key multiple times should create new immutable versions, similar to Camunda.

### Non-goals (YAGNI)

- Durable storage of uploaded BPMN or workflow definitions (in-memory only).
- Multi-tenant deployments.
- Approval workflows, RBAC, or audit trails.
- Deep BPMN validation beyond what `IBpmnConverter` already enforces.
- External clients uploading via HTTP API (UI is the primary path).

### Current context

- `Fleans.Web` already supports selecting `.bpmn/.xml`, converting via `IBpmnConverter`, and registering via `WorkflowEngine`.
- `Fleans.Api` exposes `POST /Workflow/upload-bpmn`, but this design focuses on **Web UI only** (no external API usage requirement).
- Workflows are currently stored in-memory in `WorkflowInstanceFactoryGrain`, keyed by `WorkflowId`, and are lost on silo restart.

### Requirements / success criteria

- Uploading a BPMN file with `<process id="X">` creates a new deployed definition:
  - `processDefinitionKey = X`
  - `version` auto-increments per key (1, 2, 3, ...)
  - `processDefinitionId` is unique and readable
- UI shows:
  - Latest version per key by default
  - An advanced option to start older versions
- Starting:
  - Start-by-key starts latest version (default)
  - Start-by-id starts a specific version

### Camunda-like semantics we emulate

- BPMN process id is the **definition key**.
- Deploying a resource with an existing key creates a **new version** (auto-increment).
- Starting by key starts **latest**.
- Older versions remain available for new instances; running instances keep the definition they started with.

## Design

### Identity model

We distinguish:

- **processDefinitionKey**: BPMN `<process id="...">` (example: `invoice`)
- **version**: integer, monotonically increasing per key (example: `3`)
- **processDefinitionId**: unique identifier for a deployed definition, used for execution references and "start older version"

Chosen `processDefinitionId` format (hybrid, readable + collision-resistant):

- `"<key>:<version>:<timestamp>"`
- Example: `invoice:3:20260114T132045.123Z`

Notes:

- Versions reset after silo restart (in-memory constraint).
- Deployed definitions are treated as immutable once stored.

### Storage & indexing (in-memory)

Replace the flat `_workflows: Dictionary<string, IWorkflowDefinition>` with a versioned store:

- `_byId: Dictionary<string, ProcessDefinition>`
- `_byKey: Dictionary<string, List<ProcessDefinition>>` sorted by version ascending (or maintained so latest is accessible quickly)

Where `ProcessDefinition` includes:

- `string ProcessDefinitionId`
- `string ProcessDefinitionKey`
- `int Version`
- `WorkflowDefinition Workflow`
- `DateTimeOffset DeployedAt`

### Grain API changes

Introduce deploy- and version-aware operations on `IWorkflowInstanceFactoryGrain` / `WorkflowInstanceFactoryGrain`:

- `DeployWorkflow(WorkflowDefinition workflow, DateTimeOffset deployedAt) -> ProcessDefinitionSummary`
  - Computes next `version` inside the grain (safe under concurrency due to Orleans single-threaded grain execution).
  - Generates `processDefinitionId`.
  - Stores and returns summary metadata.

- `GetAllProcessDefinitions() -> IReadOnlyList<ProcessDefinitionSummary>`
  - Used by UI to list keys, latest versions, and version history.

- `StartLatest(string processDefinitionKey) -> IWorkflowInstance`
  - Resolves latest version and starts an instance using that definition.

- `CreateWorkflowInstanceGrainByProcessDefinitionId(string processDefinitionId) -> IWorkflowInstance`
  - Starts a new instance for a specific deployed definition.

Compatibility note:

- Existing methods can be kept temporarily (adapters) but the UI should move to key/id-based starts.

### Application layer (`WorkflowEngine`)

Add corresponding façade methods:

- `DeployWorkflow(WorkflowDefinition workflow) -> ProcessDefinitionSummary`
- `StartLatest(processDefinitionKey) -> Guid`
- `StartByProcessDefinitionId(processDefinitionId) -> Guid`
- `GetAllProcessDefinitions() -> IReadOnlyList<ProcessDefinitionSummary>`

### Web UI (`Fleans.Web`)

Update `Workflows.razor` to:

- Upload flow:
  - Select `.bpmn`/`.xml`
  - Convert via `IBpmnConverter`
  - Deploy via `WorkflowEngine.DeployWorkflow(...)`
  - Show success message including key + version + id
  - Refresh list

- Listing:
  - Group by `processDefinitionKey`
  - Show latest version and counts
  - Show “Start (latest)” for each key

- Advanced action: “Start older version…”
  - UI affordance: modal or row-expander
  - Dropdown listing versions newest → oldest (display: `vN — deployedAt`)
  - Starts by `processDefinitionId`

### Error handling

- **File validation**: extension and size in UI; show actionable error message.
- **Conversion errors** (`InvalidOperationException` from converter): show “Invalid BPMN: <message>”.
- **Orleans / service failure**: show retryable error (“Unable to reach workflow engine. Try again.”).

### Testing

Add unit tests in `Fleans.Application.Tests` for `WorkflowInstanceFactoryGrain`:

- Deploy same key twice increments version and produces distinct ids.
- `StartLatest(key)` uses highest version.
- `StartByProcessDefinitionId(id)` uses the specified version.
- `GetAllProcessDefinitions()` returns correct grouping/summaries.

### Rollout plan

- Implement grain + engine changes behind new methods.
- Update `Workflows.razor` to use the new APIs.
- Keep the existing “register workflow by id” path only as a transitional internal API if needed.

