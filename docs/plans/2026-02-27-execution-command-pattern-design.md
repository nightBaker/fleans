# Execution Command Pattern Design

## Problem

The current `Activity.ExecuteAsync()` method is imperative — activities call back into `IWorkflowExecutionContext` to register timers, message subscriptions, open subprocess scopes, and start child workflows. This creates tight coupling between domain activities and workflow infrastructure, making activities hard to test and the execution model hard to extend for future features like multi-instance.

Different activity categories (subprocess, boundary events, catch events, call activity) each have bespoke handling scattered across `WorkflowInstance`. Adding a new category (e.g., multi-instance) requires touching many places.

## Solution

Change `Activity.ExecuteAsync()` to return a list of **execution commands** — declarative objects that describe what should happen. `WorkflowInstance` processes these commands uniformly via a command processor. Activities become declaration machines that express intent; the workflow engine decides how to fulfill it.

## Design

### Command Types

```csharp
public interface IExecutionCommand { }

public record CompleteCommand() : IExecutionCommand;

public record SpawnActivityCommand(
    Activity Activity,
    Guid? ScopeId,
    Guid? HostActivityInstanceId) : IExecutionCommand;

public record OpenSubProcessCommand(
    SubProcess SubProcess,
    Guid ParentVariablesId) : IExecutionCommand;

public record RegisterTimerCommand(
    string TimerActivityId,
    TimeSpan DueTime,
    bool IsBoundary) : IExecutionCommand;

public record RegisterMessageCommand(
    Guid VariablesId,
    string MessageDefinitionId,
    string ActivityId,
    bool IsBoundary) : IExecutionCommand;

public record RegisterSignalCommand(
    string SignalName,
    string ActivityId,
    bool IsBoundary) : IExecutionCommand;

public record StartChildWorkflowCommand(
    CallActivity CallActivity) : IExecutionCommand;

public record AddConditionsCommand(
    string[] SequenceFlowIds,
    List<ConditionEvaluation> Evaluations) : IExecutionCommand;

public record ConditionEvaluation(
    string SequenceFlowId,
    string Condition);

public record ThrowSignalCommand(
    string SignalName) : IExecutionCommand;
```

### Activity.ExecuteAsync Signature Change

```csharp
// Before:
internal virtual async Task ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)

// After:
internal virtual async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
```

`IWorkflowExecutionContext` is kept in the signature but slimmed to read-only — all 9 imperative/mutating methods are removed. Activities can still read state (e.g., `GetWorkflowInstanceId()` for domain events, `GetCompletedActivities()` for join gateways) but express mutations as commands.

`GetNextActivities()` stays unchanged — same signature, same role.

### IWorkflowExecutionContext Changes

Methods removed (replaced by commands):
- `StartChildWorkflow()`
- `OpenSubProcessScope()`
- `RegisterTimerReminder()`
- `RegisterMessageSubscription()`
- `RegisterBoundaryMessageSubscription()`
- `RegisterSignalSubscription()`
- `RegisterBoundarySignalSubscription()`
- `ThrowSignal()`
- `AddConditionSequenceStates()`

Methods kept (read-only state access):
- `GetWorkflowInstanceId()` — needed by base Activity for domain events
- `GetConditionSequenceStates()` — needed by gateways in GetNextActivities
- `GetActiveActivities()` / `GetCompletedActivities()` — needed by join gateways
- `GetVariable()` — needed for variable resolution
- `Complete()` — needed for external callbacks (timer fired, message delivered, etc.)
- `SetConditionSequenceResult()` — needed for external callback (condition evaluated)

### IActivityExecutionContext

No changes. Stays as-is.

### Activity-to-Command Mapping

| Activity | Returns |
|---|---|
| StartEvent | `[CompleteCommand]` |
| EndEvent | `[CompleteCommand]` |
| TaskActivity | `[CompleteCommand]` |
| ScriptTask | `[]` (empty — publishes EvaluateScriptEvent via activityContext, externally completed) |
| SubProcess | `[OpenSubProcessCommand(this, parentVariablesId)]` + boundary registrations |
| CallActivity | `[StartChildWorkflowCommand(this)]` + boundary registrations |
| ExclusiveGateway | `[AddConditionsCommand(...)]` or `[CompleteCommand]` if no conditions |
| ParallelGateway (fork) | `[CompleteCommand]` |
| ParallelGateway (join) | `[CompleteCommand]` if all paths done, else `[]` |
| EventBasedGateway | `[CompleteCommand]` |
| TimerIntermediateCatchEvent | `[RegisterTimerCommand(activityId, duration, false)]` + boundary registrations |
| MessageIntermediateCatchEvent | `[RegisterMessageCommand(varsId, msgDefId, activityId, false)]` + boundary registrations |
| SignalIntermediateCatchEvent | `[RegisterSignalCommand(signalName, activityId, false)]` + boundary registrations |
| SignalIntermediateThrowEvent | `[ThrowSignalCommand(signalName), CompleteCommand]` |
| BoundaryTimerEvent | `[CompleteCommand]` |
| BoundaryMessageEvent | `[CompleteCommand]` |
| BoundarySignalEvent | `[CompleteCommand]` |

### BoundarableActivity

Appends boundary registration commands from the workflow definition:

```csharp
internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(
    IWorkflowExecutionContext workflowContext,
    IActivityExecutionContext activityContext,
    IWorkflowDefinition definition)
{
    var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();
    // Subclass commands inserted here via override
    commands.AddRange(BuildBoundaryRegistrationCommands(activityContext, definition));
    return commands;
}
```

`BuildBoundaryRegistrationCommands()` scans the definition for `BoundaryTimerEvent`, `MessageBoundaryEvent`, and `SignalBoundaryEvent` attached to this activity and produces the corresponding `RegisterTimerCommand`, `RegisterMessageCommand`, `RegisterSignalCommand` (all with `IsBoundary = true`).

### Command Processor in WorkflowInstance

```csharp
private async Task ProcessCommands(
    IReadOnlyList<IExecutionCommand> commands,
    ActivityInstanceEntry entry,
    IActivityExecutionContext activityContext)
{
    foreach (var command in commands)
    {
        switch (command)
        {
            case CompleteCommand:
                await activityContext.Complete();
                break;

            case SpawnActivityCommand spawn:
                AddActivityEntry(spawn.Activity, spawn.ScopeId, spawn.HostActivityInstanceId);
                break;

            case OpenSubProcessCommand sub:
                CreateChildVariableScope(entry.ActivityInstanceId, sub.ParentVariablesId);
                var startEvent = sub.SubProcess.Activities.OfType<StartEvent>().First();
                AddActivityEntry(startEvent, scopeId: entry.ActivityInstanceId,
                    hostActivityInstanceId: entry.ActivityInstanceId);
                break;

            case RegisterTimerCommand timer:
                await RegisterTimerReminder(entry.ActivityInstanceId,
                    timer.TimerActivityId, timer.DueTime);
                break;

            case RegisterMessageCommand msg:
                if (msg.IsBoundary)
                    await RegisterBoundaryMessageSubscription(msg.VariablesId,
                        entry.ActivityInstanceId, msg.ActivityId, msg.MessageDefinitionId);
                else
                    await RegisterMessageSubscription(msg.VariablesId,
                        msg.MessageDefinitionId, msg.ActivityId);
                break;

            case RegisterSignalCommand sig:
                if (sig.IsBoundary)
                    await RegisterBoundarySignalSubscription(
                        entry.ActivityInstanceId, sig.ActivityId, sig.SignalName);
                else
                    await RegisterSignalSubscription(sig.SignalName, sig.ActivityId,
                        entry.ActivityInstanceId);
                break;

            case StartChildWorkflowCommand child:
                await StartChildWorkflow(child.CallActivity, activityContext);
                break;

            case AddConditionsCommand cond:
                await AddConditionSequenceStates(entry.ActivityInstanceId, cond.SequenceFlowIds);
                await PublishConditionEvaluationEvents(cond.Evaluations, entry, activityContext);
                break;

            case ThrowSignalCommand sig:
                await ThrowSignal(sig.SignalName);
                break;
        }
    }
}
```

### ExecuteWorkflow Loop Change

```csharp
// In the execution loop, after getting the activity and context:
var commands = await activity.ExecuteAsync(workflowContext, activityContext, definition);
await ProcessCommands(commands, entry, activityContext);
```

The rest of the loop (`TransitionToNextActivity`, `CompleteFinishedSubProcessScopes`, `WriteStateAsync`) stays unchanged.

### What Does NOT Change

- `GetNextActivities()` — same signature, same role, same implementation
- `TransitionToNextActivity()` — stays as-is
- `CompleteFinishedSubProcessScopes()` — stays as-is
- External callback handlers (`HandleTimerFired`, `HandleMessageDelivery`, `HandleSignalDelivery`, `OnChildWorkflowCompleted`, `OnChildWorkflowFailed`) — stay imperative, they respond to infrastructure events
- `BoundaryEventHandler` — stays as-is
- `ActivityInstanceEntry`, `ActivityInstanceGrain` — stay as-is
- Variable scope system — stays as-is
- Domain events — stay as-is (published from base Activity via activityContext)

### File Impact

- `Fleans.Domain/Activities/*.cs` — all 17 activity files (return commands instead of calling workflowContext)
- `Fleans.Domain/IWorkflowExecutionContext.cs` — remove 9 imperative methods
- `Fleans.Domain/ExecutionCommands.cs` — new file with IExecutionCommand + ~10 command records
- `Fleans.Application/Grains/WorkflowInstance.cs` — add `ProcessCommands()`, update `ExecuteWorkflow()` loop
- `Fleans.Domain.Tests/` — update test assertions for new return types

## Design Decisions

**Why keep IWorkflowExecutionContext in Execute (read-only)?**
The base `Activity.ExecuteAsync` needs `workflowContext.GetWorkflowInstanceId()` to publish `WorkflowActivityExecutedEvent`. `ParallelGateway` join needs `GetCompletedActivities()` to decide whether to complete. Removing workflowContext entirely would require either moving event publishing to the processor or adding these methods to `IActivityExecutionContext`. Keeping it read-only is the pragmatic choice.

**Why commands instead of a flat result record?**
A flat `ExecutionResult` with `List<SpawnedActivity>`, `List<BoundaryRegistration>`, `ChildWorkflowRequest?`, etc. has many optional fields. Commands are self-describing — each one carries exactly the data it needs, no nullability questions. Adding new command types is additive.

**Why not commands for external callbacks too?**
External callbacks (timer fired, message delivered) are infrastructure responses that mutate state imperatively. They're a different concern from the declarative Execute path. Forcing them into commands would add complexity without benefit.

**Why OpenSubProcessCommand instead of SpawnActivityCommand?**
SubProcess execution requires creating a child variable scope AND spawning the StartEvent. A generic `SpawnActivityCommand` doesn't capture scope creation. `OpenSubProcessCommand` is a higher-level intent that the processor expands.

## Future Enablement

- **Multi-instance:** A `MultiInstanceActivity` wrapper returns N `SpawnActivityCommand`s, one per collection item, each with host ownership. Completion semantics handled by the wrapper's `GetNextActivities`.
- **Non-interrupting boundaries:** The command processor can decide not to cancel the host activity based on a flag on the boundary registration command.
- **Activity middleware/interceptors:** Can inspect/modify the command list before processing.
- **Better testability:** Activity unit tests assert on returned command lists — no mocking of IWorkflowExecutionContext needed for Execute behavior.
