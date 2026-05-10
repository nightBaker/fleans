# Manual Test Plan — #517: IO Mapping Editor Gaps

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`

## Test steps

| # | Action | Expected |
|---|--------|----------|
| 1 | Import `tests/manual/06-call-activity/parent-process.bpmn`, click `callChild` | Input Mappings table shows "input → input" row; Output Mappings shows "result → result" row |
| 2 | Click "Add Input Mapping" on `callChild` | New empty row appears; XML gains `fleans:InputMapping` element |
| 3 | Edit the new row source/target, click elsewhere | XML updates with the new values |
| 4 | Click "Remove" on the new row | Row disappears; XML entry removed |
| 5 | Import `tests/manual/37-custom-task-framework/stub-custom-task.bpmn` with **no worker silo running**, click `ct1` | Panel shows plugin warning ("not registered") + info bar + Input/Output Mappings tables with existing `zeebe:input`/`zeebe:output` entries visible |
| 6 | Click "Add Output Mapping" on `ct1` | New empty row appears in Output Mappings |
| 7 | Click a regular SubProcess (no IO mappings) | No Input/Output Mappings tables shown (ServiceTask-only feature) |
