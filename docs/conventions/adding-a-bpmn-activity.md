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
