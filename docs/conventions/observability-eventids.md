# Observability EventId ranges

Contributor-facing reference for the structured-logging EventId allocation. Allocate new
`[LoggerMessage]` EventIds from the appropriate range below. New ranges must be added
here when a new class starts emitting structured logs.

For the architectural background (filter pattern, scope-field semantics, RequestContext
propagation), see [`docs/plans/2026-02-08-structured-workflow-logging.md`](../plans/2026-02-08-structured-workflow-logging.md).

## Allocated ranges

| Range | Class | Notes |
|-------|-------|-------|
| 1000–1199 | `WorkflowInstance` | Sub-ranges: 1066 UserTaskClaim rejection (Warning); 1070–1079 pending events / event sub-processes; 1078 root-scope listeners; 1080–1089 complex gateway; 1090–1099 escalation; 1100–1109 transaction sub-process; 1110–1119 compensation |
| 2000–2099 | `ActivityInstance` | |
| 3000–3099 | `WorkflowInstanceState` | Includes 3030–3032 escalation warnings |
| 4000–4099 | Event handlers | `Fleans.Application/Events/Handlers/*` |
| 5000–5099 | `WorkflowEventsPublisher` | |
| 6000–6099 | `WorkflowInstanceFactoryGrain` | |
| 7000–7099 | `WorkflowEngine` | |
| 8000–8099 | `TimerStartEventSchedulerGrain` | |
| 9000–9099 | `BpmnConverter` | |
| 10000–10099 | `TimerCallbackGrain` | |
| 11000–11099 | `KafkaProductionPresetExtensions` | 11000 preset-applied INFO |
| 11100–11199 | `KafkaQueueAdapter`, `KafkaQueueAdapterReceiver`, `KafkaQueueAdapterFactory` | 11100 SSL-no-paths OS-trust-store WARNING (shared EventId across all three sibling classes) |
| 11200–11299 | `MskIamTokenRefresher` | 11200 token-refresh-failed ERROR, 11201 set-token-failure-threw WARNING |

## Adding a new range

1. Pick an unused range (e.g., 11000–11099) in increments of 100.
2. Add a row to the table above with the class name.
3. Add the row to the website-published Logs & Debugging reference if user-facing — see
   `website/src/content/docs/reference/troubleshooting-logs-debugging.md`.

## Log-scope fields

Every grain call gets a scope wrapping (via `WorkflowLoggingScopeFilter`). The scope fields
are propagated through `RequestContext`:

- `WorkflowId`
- `ProcessDefinitionId`
- `WorkflowInstanceId`
- `ActivityId`
- `ActivityInstanceId`
- `VariablesId`

These are the queryable fields in structured-log backends. Filter by them, not by EventId
numeric ranges, in user-facing log queries.
