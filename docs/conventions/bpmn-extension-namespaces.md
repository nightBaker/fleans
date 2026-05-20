# BPMN extension namespace policy

The parser accepts three URIs for the engine's extension elements (`taskDefinition`, `ioMapping > input/output`, `subscription`, `expectedOutputs`, multi-instance loop attrs, `correlationKey`):

| URI | Prefix | Status |
|---|---|---|
| `https://fleans.io/schema/bpmn/1.0` | `fleans:` | **Current.** The editor writes new BPMN here. |
| `http://fleans.io/schema/bpmn/fleans` | `fleans:` | **Legacy.** Kept for back-compat — see `BpmnNamespaces.FleansLegacy`. Remove once in-the-wild files are gone. |
| `http://camunda.org/schema/zeebe/1.0` | `zeebe:` | Kept indefinitely so files exported from Camunda's modeler still deploy. |

All parser sites probe namespaces via `BpmnNamespaces.FindExtensionElement` / `GetExtensionAttributeValue` (probe order: `Fleans`, `FleansLegacy`, `Zeebe`).

## `<fleans:expectedOutputs>` shape

Inside `<fleans:expectedOutputs>` the child element is `<fleans:expectedOutput name="…">`. The legacy `<fleans:output name="…">` shape is still parsed but **no longer written**.
