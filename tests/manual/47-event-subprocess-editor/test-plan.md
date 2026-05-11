# Manual Test Plan — #516: Event Sub-Process editor toggle

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`

## Test steps

| # | Action | Expected |
|---|--------|----------|
| 1 | Import `tests/manual/19-event-subprocess-error/error-event-subprocess.bpmn`, click `errorEventSub` | Panel header shows "Event Sub-Process"; "Triggered by event" checkbox is **checked** |
| 2 | Import `tests/manual/07-subprocess/embedded-subprocess.bpmn`, click the SubProcess | Panel header shows "Sub-Process"; "Triggered by event" checkbox is **unchecked** |
| 3 | With the regular SubProcess selected, check "Triggered by event" | Element redraws with dashed border; XML gains `triggeredByEvent="true"` |
| 4 | With an Event SubProcess selected, uncheck "Triggered by event" | Element redraws with solid border; XML sets `triggeredByEvent="false"` (element behaves as regular Sub-Process) |
| 5 | Place a new SubProcess from the palette, click it | Checkbox unchecked, label "Sub-Process" |
| 6 | Toggle "Triggered by event" on the new SubProcess, then undo (Ctrl+Z) | Checkbox reverts; border reverts; XML correct |
