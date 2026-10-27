# Manual Test Plan — IO Mapping Editor (#517, #950)

The properties panel's **Input/Output mapping** section edits `fleans:ioMapping` on every element
the engine accepts it on; a call activity's own `fleans:inputMapping` / `fleans:outputMapping`
are edited in a separate **Child process mappings** section.

Automated in `Fleans.E2E.Tests/Specs/EditorIoMappingTests.cs`: adding an input and an output to a
script task in the panel → deploy from the editor → the instance gets the mapped values; and, on
a call activity, removing an ioMapping input leaves the child-process mapping untouched. The steps
below cover the rest.

## Prerequisites

Aspire stack running: `dotnet run --project Fleans.Aspire` from `src/Fleans/`

## Test steps

| # | Action | Expected |
|---|--------|----------|
| 1 | In `/editor`, add one element of each kind and select it in turn: task, user task, sub-process, transaction, call activity | **Input/Output mapping** section shows both **Inputs** and **Outputs** |
| 2 | Select a start event, an intermediate catch event and a boundary event | Section shows **Outputs** only |
| 3 | Select an intermediate throw event and an end event | Section shows **Inputs** only |
| 4 | Select a gateway, a sequence flow, an event sub-process (sub-process with *Triggered by event* checked), a send task | No Input/Output mapping section |
| 5 | Import `tests/manual/37-custom-task-framework/stub-custom-task.bpmn` with **no worker silo running**, click `ct1` | Plugin "not registered" warning; the section lists its existing `zeebe:input` / `zeebe:output` entries |
| 6 | On `ct1`, click **Add output**, fill `=x` → `y`, switch to XML mode | The new `<zeebe:output source="=x" target="y" />` sits inside the existing `<zeebe:ioMapping>` — no `fleans:` child mixed in, no second container |
| 7 | On any task, add an input, type target `1abc` | Red hint *Target must be a variable name…*; with target `ok` and source `=` the hint says *Add an expression after '='* |
| 8 | Remove every row on an element, switch to XML mode | No empty `<fleans:ioMapping />` left behind |
| 9 | Deploy a process with mappings, start an instance, open it on **Process Instances**, click the mapped element | Section shows the mappings read-only (no Add/Remove buttons) |
