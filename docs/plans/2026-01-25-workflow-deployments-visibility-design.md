## Workflow deployments visibility (split view) design

### Status

Draft validated via discussion on 2026-01-25.

### Goal

Improve visibility for workflow deployments and versions in the admin UI with a
split view: keys on the left, versions for the selected key on the right. Add
search by process key and actions to start latest or a selected version.

### Non-goals (YAGNI)

- New backend APIs or storage changes.
- Server-side pagination or filters.
- Tagging, labels, or audit trails.
- Real-time streaming updates.

### Current context

- The BPMN versioning model (key/version/id) is defined in
  `2026-01-14-bpmn-upload-design.md`.
- `WorkflowEngine.GetAllProcessDefinitions()` returns all definitions.
- Starting supports both latest-by-key and by processDefinitionId.

### Options considered

1. Client-side grouping (recommended, selected)
   - Use the existing GetAllProcessDefinitions data.
   - Group and filter in the UI.
2. Split API (keys + versions)
   - Add new grain methods and UI calls.
3. Streamed updates
   - Subscribe to changes for real-time refresh.

Selected: Option 1, minimal change and lowest risk.

## Design

### UX layout and data model

- Split view:
  - Left pane: processDefinitionKey list with latest version and version count.
  - Right pane: versions for selected key, sorted newest to oldest.
- Search box filters keys by substring (case-insensitive).
- Actions:
  - "Start latest" on key rows (left pane).
  - "Start selected version" in right pane (enabled on selection).

### Components and data flow

1. Load all definitions once via `GetAllProcessDefinitions()`.
2. Group by ProcessDefinitionKey, compute:
   - LatestVersion (max version)
   - VersionCount
   - Definitions sorted descending by version
3. Search filters keys list only.
4. Selecting a key updates right pane.
5. Start actions call:
   - `WorkflowEngine.StartLatest(key)`
   - `WorkflowEngine.StartByProcessDefinitionId(id)`

### Error handling and empty states

- Load failure: show retryable error message and a Retry action.
- Start failure: show toast error, keep selection unchanged.
- Empty states:
  - No deployments: "No process definitions yet."
  - No search results: "No keys match '<query>'."
  - No versions for key: "No versions found for this key."

### Testing

- Unit tests for grouping, sorting, and search filtering.
- Integration tests for "Start latest" and "Start selected version" calls.
- Manual smoke test: load list, filter, select, start.

### Rollout

- UI-only change using existing APIs.
- No data migration or backend changes required.
