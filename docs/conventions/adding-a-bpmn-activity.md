# Adding a new BPMN activity

Use this guide when the new behavior is a first-class BPMN concept (gateway, event, structural activity). For plugin-shaped behavior (REST call, email, custom external system) use [Adding a custom-task plugin](./adding-a-custom-task-plugin.md) instead.

## Steps

1. Create the activity class in `Fleans.Domain/Activities/` — extend the existing `Activity` base class.
2. Register it in `Fleans.Infrastructure/Bpmn/BpmnConverter.cs` so BPMN XML maps to it.
   If it carries a condition or script, add it to `Fleans.Application/Deployments/WorkflowExpressionValidator.cs` so a deploy compiles it (#975).
3. Add tests in `Fleans.Domain.Tests/` using Orleans `TestCluster`. **Every activity must have tests that verify workflow state after both completion and failure** — see `ScriptTaskTests.cs` and `TaskActivityTests.cs` for the pattern:
   - **Completion tests:** completed activities list, variable merging (count + values), activity instance marked completed, no active activities remain, sequential chaining.
   - **Fail tests:** error state set with correct code/message (500 for generic Exception, 400 for BadRequestActivityException), failed activity does **not** transition to its outgoing flows (`WorkflowExecution.ResolveTransitions` skips failed and cancelled entries — an error boundary catches it; otherwise the workflow fails once nothing else is active), no variables merged on failure.
4. Update the BPMN elements table in `README.md`.
5. Add a manual test plan under `tests/manual/NN-<name>/` and append it to `tests/manual/README.md` (canonical catalog).
6. Add a website docs page or update an existing one under `website/src/content/docs/`.

## Adding a script language

A new `scriptFormat` needs **no engine change** (#1105). C# (`csharp`, `c#` or omitted) is the only built-in format; `ScriptTask` sends every other one through the custom-task path as task type `fleans.script.<format>` — the format in lower case, with the aliases in `Fleans.Domain/ScriptFormats.cs` (`js` → `javascript`, `py` → `python`). To support a language, write an external-task worker for that task type (see [Adding a custom-task plugin](./adding-a-custom-task-plugin.md)) that:

- registers `fleans.script.<format>` with `all_variables` (`ExternalTaskVariables.All` in the .NET SDK);
- reads the script text from the reserved input `script` (`inputs_json`) and the variables from `variables_json`;
- completes with **only the top-level variables the script changed**, so untouched ones keep their engine-side type;
- fails with code `500` and the script's message (any exception in the .NET SDK does this), or with a BPMN error code to reach an error boundary;
- bounds the script's time, memory and recursion itself, and caches parsed scripts by their text.

The contract is documented at the top of `Fleans.ExternalTasks.Sdk/Protos/external_task_worker.proto`. The parser accepts any format; a deploy with no connected worker for its task type **warns** (`CustomTaskContractValidator`) and the task waits until one connects. `WorkflowExpressionValidator` compiles only C#; the worker compiles the rest when it runs. Script-format workers are left out of the editor's service-task plugin picker (`CustomTaskPicker`). The JavaScript worker lives in [`nightBaker/fleans-plugins`](https://github.com/nightBaker/fleans-plugins) (`Fleans.Plugins.JavaScript`).
