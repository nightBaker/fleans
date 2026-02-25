# Manual Test Plan Design

## Goal

Cover all implemented BPMN features with manual tests that can be verified through the Chrome browser (Web UI) and curl (API). Each feature gets its own folder with `.bpmn` fixture files and a `test-plan.md`.

## Folder Structure

```
tests/
  manual/
    01-basic-workflow/
      simple-workflow.bpmn
      test-plan.md
    02-script-tasks/
      script-variable-manipulation.bpmn
      test-plan.md
    03-exclusive-gateway/
      conditional-branching.bpmn
      test-plan.md
    04-parallel-gateway/
      fork-join.bpmn
      test-plan.md
    05-event-based-gateway/
      timer-vs-message-race.bpmn
      test-plan.md
    06-call-activity/
      parent-process.bpmn
      child-process.bpmn
      test-plan.md
    07-subprocess/
      embedded-subprocess.bpmn
      test-plan.md
    08-timer-events/
      timer-intermediate-catch.bpmn
      timer-boundary.bpmn
      test-plan.md
    09-message-events/
      message-catch.bpmn
      message-boundary.bpmn
      test-plan.md
    10-signal-events/
      signal-catch-throw.bpmn
      signal-boundary.bpmn
      test-plan.md
    11-error-boundary/
      error-on-call-activity.bpmn
      child-that-fails.bpmn
      test-plan.md
    12-variable-scoping/
      parallel-variable-isolation.bpmn
      test-plan.md
```

## Test Scenarios

| # | Feature | What We Test | Verification |
|---|---------|-------------|--------------|
| 01 | Basic Workflow | Deploy, start, complete: start → task → end | Instance status=Completed, 3 completed activities, no errors |
| 02 | Script Tasks | Variable creation and mutation via `_context.x = 42` and `_context.x = _context.x + 1` | Variables tab shows correct values after each script |
| 03 | Exclusive Gateway | Conditional branching with `${x > 5}` + default flow | Conditions tab shows true/false evaluations, correct path taken |
| 04 | Parallel Gateway | Fork into 2 branches, join, continue to end | Both branches execute, join waits for all, correct completion order |
| 05 | Event-Based Gateway | Timer (PT5S) vs message catch — send message before timer fires | Message path taken, timer path cancelled, correct end state |
| 06 | Call Activity | Parent calls child process with input/output variable mapping | Child completes, variables propagated back to parent, parent completes |
| 07 | SubProcess | Embedded subprocess with internal start → task → end | Subprocess activities visible in instance viewer, parent continues after |
| 08 | Timer Events | Intermediate catch (PT5S wait) + boundary timer interrupting a task | Timer fires, activity transitions, boundary timeout path taken |
| 09 | Message Events | Intermediate catch (wait for message) + boundary message interrupting a task | curl sends message with correlationKey, correct instance receives it |
| 10 | Signal Events | Throw signal from one workflow, catch in another + boundary signal | curl broadcasts signal, all waiting instances receive it |
| 11 | Error Boundary | CallActivity calls a child that throws, error boundary catches it | Error path taken, error code visible, parent doesn't fail |
| 12 | Variable Scoping | Parallel fork where each branch sets same variable to different value | Variables tab shows separate scopes per branch, no cross-contamination |

## Test Plan Template

Each `test-plan.md` follows this format:

```markdown
# Feature Name

## Scenario
Brief description of what this tests.

## Prerequisites
- Deploy child-process.bpmn first (if applicable)

## Steps

### 1. Deploy the workflow
- Open Workflows page
- Click "Create New"
- Import `filename.bpmn` via drag-drop
- Click Deploy, confirm

### 2. Start an instance
- On Workflows page, click "Start" for the deployed process
- Navigate to the instance viewer

### 3. Trigger events (if applicable)
\`\`\`bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "...", "correlationKey": "...", "variables": {}}'
\`\`\`

### 4. Verify outcome
- [ ] Instance status: Completed
- [ ] Completed activities: [list]
- [ ] Variables tab: key=value, key=value
- [ ] Conditions tab: flow X = true, flow Y = false
- [ ] No error activities (or specific error expected)
```

## Execution Approach

- Start Aspire stack via `dotnet run --project Fleans.Aspire`
- Use Chrome (via Claude in Chrome) for all UI interactions
- Use curl from terminal for API calls (messages, signals)
- Verify outcomes visually in the Instance Viewer

## BPMN Fixture Notes

- Timers use short durations (PT5S–PT10S) so tests complete quickly
- Error scenarios use ScriptTask with intentionally failing scripts
- Message events include correlationKey definitions for targeted delivery
- All fixtures use the BPMN 2.0 namespace `http://www.omg.org/spec/BPMN/20100524/MODEL`
