# Manual Test Plan — #522: EndEvent Variant Coverage

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`

## Test steps

| # | Action | Expected |
|---|--------|----------|
| 1 | Import `terminate-end.bpmn`, click `terminate_end` node | Panel type label: **"Terminate End Event"**; no editable fields |
| 2 | Import `error-end.bpmn`, click `error_end` node | Panel type label: **"Error End Event"**; "Error Code" field shows **"PAY-500"** |
| 3 | Import `signal-end.bpmn`, click `signal_end` node | Panel type label: **"Signal End Event"**; "Signal Name" field shows **"processCompleted"** |
| 4 | Import `message-end.bpmn`, click `message_end` node | Panel type label: **"Message End Event"**; "Message Name" field shows **"orderCompleted"**; **no** Correlation Key field visible |
| 5 | Import any fixture with a Message Intermediate Throw Event (if available) | Correlation Key field is **not** shown (throwing events do not correlate) |

## Run-time semantics

> Steps 6–10 are automated by `EndEventVariantsTests` (E2E) and `EndEventVariantTests` (#843).

| # | Action | Expected |
|---|--------|----------|
| 6 | Deploy and start `error-end-in-subprocess.bpmn` | The error boundary on `payment` fires; the instance completes through `handled_end`, not `normal_end` |
| 7 | Deploy and start `error-end.bpmn` | The uncaught error fails the instance |
| 8 | Deploy and start `terminate-end-parallel.bpmn` | Reaching `terminate_end` completes the instance; `wait` is cancelled, `normal_end` never runs |
| 9 | Deploy `signal-end-catcher.bpmn` and start it; then deploy and start `signal-end.bpmn` | The catcher completes when `signal_end` broadcasts `processCompleted` |
| 10 | Deploy `message-end.bpmn` | Until the message end event sends its message, the deploy is rejected rather than silently accepted |
