# Boundary Handler Extraction Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract boundary event handling (~150 lines) from `WorkflowInstance` into a `BoundaryEventHandler` service class, unifying the duplicated timer/message/error boundary patterns.

**Architecture:** `BoundaryEventHandler` service injected via DI into `WorkflowInstance`. WorkflowInstance implements `IBoundaryEventStateAccessor` to provide the handler access to state, grain factory, and workflow operations. The three boundary patterns (timer, message, error) share a unified `InterruptAndExecuteBoundaryAsync` core method.

**Tech Stack:** C# / .NET / Orleans / MSTest / NSubstitute

---

### Task 1: Create IBoundaryEventStateAccessor and IBoundaryEventHandler interfaces

**Files:**
- Create: `src/Fleans/Fleans.Application/Services/IBoundaryEventStateAccessor.cs`
- Create: `src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs`

**Step 1: Create the state accessor interface**

Create `src/Fleans/Fleans.Application/Services/IBoundaryEventStateAccessor.cs`:

```csharp
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Fleans.Application.Services;

public interface IBoundaryEventStateAccessor
{
    WorkflowInstanceState State { get; }
    IGrainFactory GrainFactory { get; }
    ILogger Logger { get; }
    IWorkflowExecutionContext WorkflowExecutionContext { get; }
    ValueTask<IWorkflowDefinition> GetWorkflowDefinition();
    ValueTask<object?> GetVariable(string variableName);
    Task TransitionToNextActivity();
    Task ExecuteWorkflow();
}
```

**Step 2: Create the handler interface**

Create `src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs`:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Application.Services;

public interface IBoundaryEventHandler
{
    void Initialize(IBoundaryEventStateAccessor accessor);
    Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId);
    Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId);
    Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId);
    Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId);
    Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, string? skipMessageName = null);
}
```

**Step 3: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/IBoundaryEventStateAccessor.cs src/Fleans/Fleans.Application/Services/IBoundaryEventHandler.cs
git commit -m "feat: add IBoundaryEventStateAccessor and IBoundaryEventHandler interfaces"
```

---

### Task 2: Implement BoundaryEventHandler

**Files:**
- Create: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`

**Step 1: Create the implementation**

Create `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs`. This moves logic from `WorkflowInstance` lines 124-157, 224-255, 531-568, and 719-763 into a unified service.

```csharp
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Services;

public partial class BoundaryEventHandler : IBoundaryEventHandler
{
    private IBoundaryEventStateAccessor _accessor = null!;

    public void Initialize(IBoundaryEventStateAccessor accessor)
    {
        _accessor = accessor;
    }

    public async Task HandleBoundaryTimerFiredAsync(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
    {
        var attachedActivityId = boundaryTimer.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
            return; // Activity already completed, timer is stale

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Complete();
        _accessor.State.CompleteEntries([attachedEntry]);

        // Timer fired, so only unsubscribe message boundaries
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId);
        LogBoundaryTimerInterrupted(boundaryTimer.ActivityId, attachedActivityId);

        // Create and execute boundary timer event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryTimer, attachedInstance);
    }

    public async Task HandleBoundaryMessageFiredAsync(MessageBoundaryEvent boundaryMessage, Guid hostActivityInstanceId)
    {
        var definition = await _accessor.GetWorkflowDefinition();
        var attachedActivityId = boundaryMessage.AttachedToActivityId;

        // Check if attached activity is still active (lookup by instance ID)
        var attachedEntry = _accessor.State.Entries.FirstOrDefault(e =>
            e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
        if (attachedEntry == null)
            return; // Activity already completed, message is stale

        // Interrupt the attached activity
        var attachedInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(attachedEntry.ActivityInstanceId);
        await attachedInstance.Complete();
        _accessor.State.CompleteEntries([attachedEntry]);

        // Clean up all boundary events for the interrupted activity
        await UnregisterBoundaryTimerRemindersAsync(attachedActivityId, attachedEntry.ActivityInstanceId);
        // Unsubscribe other boundary messages, but skip the one that fired
        // (its subscription was already removed by DeliverMessage, and calling
        // back into the same correlation grain would deadlock)
        var firedMessageDef = definition.Messages.First(m => m.Id == boundaryMessage.MessageDefinitionId);
        await UnsubscribeBoundaryMessageSubscriptionsAsync(attachedActivityId, skipMessageName: firedMessageDef.Name);
        LogBoundaryMessageInterrupted(boundaryMessage.ActivityId, attachedActivityId);

        // Create and execute boundary message event instance
        await CreateAndExecuteBoundaryInstanceAsync(boundaryMessage, attachedInstance);
    }

    public async Task HandleBoundaryErrorAsync(string activityId, BoundaryErrorEvent boundaryError, Guid activityInstanceId)
    {
        LogBoundaryEventTriggered(boundaryError.ActivityId, activityId);

        var activityGrain = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(activityInstanceId);
        await CreateAndExecuteBoundaryInstanceAsync(boundaryError, activityGrain);
    }

    public async Task UnregisterBoundaryTimerRemindersAsync(string activityId, Guid hostActivityInstanceId)
    {
        var definition = await _accessor.GetWorkflowDefinition();

        foreach (var boundaryTimer in definition.Activities.OfType<BoundaryTimerEvent>()
            .Where(bt => bt.AttachedToActivityId == activityId))
        {
            var instanceId = _accessor.State.Id;
            var callbackGrain = _accessor.GrainFactory.GetGrain<ITimerCallbackGrain>(
                instanceId, $"{hostActivityInstanceId}:{boundaryTimer.ActivityId}");
            await callbackGrain.Cancel();
            LogTimerReminderUnregistered(boundaryTimer.ActivityId);
        }
    }

    public async Task UnsubscribeBoundaryMessageSubscriptionsAsync(string activityId, string? skipMessageName = null)
    {
        var definition = await _accessor.GetWorkflowDefinition();

        foreach (var boundaryMsg in definition.Activities.OfType<MessageBoundaryEvent>()
            .Where(bm => bm.AttachedToActivityId == activityId))
        {
            var messageDef = definition.Messages.FirstOrDefault(m => m.Id == boundaryMsg.MessageDefinitionId);
            if (messageDef?.CorrelationKeyExpression is null) continue;
            if (messageDef.Name == skipMessageName) continue;

            var correlationValue = await _accessor.GetVariable(messageDef.CorrelationKeyExpression);
            if (correlationValue is null) continue;

            var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
            await correlationGrain.Unsubscribe(correlationValue.ToString()!);
        }
    }

    private async Task CreateAndExecuteBoundaryInstanceAsync(Activity boundaryActivity, IActivityInstanceGrain sourceInstance)
    {
        var boundaryInstanceId = Guid.NewGuid();
        var boundaryInstance = _accessor.GrainFactory.GetGrain<IActivityInstanceGrain>(boundaryInstanceId);
        var variablesId = await sourceInstance.GetVariablesStateId();
        await boundaryInstance.SetActivity(boundaryActivity.ActivityId, boundaryActivity.GetType().Name);
        await boundaryInstance.SetVariablesId(variablesId);

        var boundaryEntry = new ActivityInstanceEntry(boundaryInstanceId, boundaryActivity.ActivityId, _accessor.State.Id);
        _accessor.State.AddEntries([boundaryEntry]);

        await boundaryActivity.ExecuteAsync(_accessor.WorkflowExecutionContext, boundaryInstance);
        await _accessor.TransitionToNextActivity();
        await _accessor.ExecuteWorkflow();
    }

    [LoggerMessage(EventId = 1019, Level = LogLevel.Information, Message = "Timer reminder unregistered for {TimerActivityId}")]
    private partial void LogTimerReminderUnregistered(string timerActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Boundary timer {BoundaryTimerId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryTimerInterrupted(string boundaryTimerId, string attachedActivityId);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Information, Message = "Boundary message {BoundaryMessageId} interrupted attached activity {AttachedActivityId}")]
    private partial void LogBoundaryMessageInterrupted(string boundaryMessageId, string attachedActivityId);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Information, Message = "Boundary event {BoundaryEventId} triggered on activity {ActivityId}")]
    private partial void LogBoundaryEventTriggered(string boundaryEventId, string activityId);
}
```

**Important note on LoggerMessage:** The `partial class` needs an `ILogger` field. Since the handler is initialized with an accessor, add a logger property:

Add after `private IBoundaryEventStateAccessor _accessor = null!;`:

```csharp
    private ILogger Logger => _accessor.Logger;
```

However, `[LoggerMessage]` source generators require the logger field to be named `_logger` or to use `LoggerMessage(... , LoggerField = "...")`. The simplest approach is to add a private `_logger` property:

Actually, for `[LoggerMessage]` to work on a partial class, we need to add a private `ILogger` field. Update the class to:

```csharp
public partial class BoundaryEventHandler : IBoundaryEventHandler
{
    private IBoundaryEventStateAccessor _accessor = null!;
    private ILogger _logger = null!;

    public void Initialize(IBoundaryEventStateAccessor accessor)
    {
        _accessor = accessor;
        _logger = accessor.Logger;
    }
    // ... rest of the code
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs
git commit -m "feat: implement BoundaryEventHandler with unified boundary pattern"
```

---

### Task 3: Register BoundaryEventHandler in DI and wire into WorkflowInstance

**Files:**
- Modify: `src/Fleans/Fleans.Application/ApplicationDependencyInjection.cs:9` — register handler
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs` — implement accessor, inject handler, delegate calls

**Step 1: Register in DI**

In `src/Fleans/Fleans.Application/ApplicationDependencyInjection.cs`, add the handler registration:

```csharp
using Fleans.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Application
{
    public static class ApplicationDependencyInjection
    {
        public static void AddApplication(this IServiceCollection services)
        {
            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
            services.AddTransient<IBoundaryEventHandler, BoundaryEventHandler>();
        }
    }
}
```

**Step 2: Update WorkflowInstance**

Make the following changes to `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`:

**2a. Add using and implement IBoundaryEventStateAccessor:**

Change line 12 from:
```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain
```
to:
```csharp
public partial class WorkflowInstance : Grain, IWorkflowInstanceGrain, IBoundaryEventStateAccessor
```

Add `using Fleans.Application.Services;` at the top.

**2b. Add handler field and IBoundaryEventStateAccessor members:**

Update constructor (lines 23-31) to accept `IBoundaryEventHandler`:

```csharp
    private readonly IBoundaryEventHandler _boundaryHandler;

    public WorkflowInstance(
        [PersistentState("state", GrainStorageNames.WorkflowInstances)] IPersistentState<WorkflowInstanceState> state,
        IGrainFactory grainFactory,
        ILogger<WorkflowInstance> logger,
        IBoundaryEventHandler boundaryHandler)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
        _boundaryHandler = boundaryHandler;
        _boundaryHandler.Initialize(this);
    }
```

Add the `IBoundaryEventStateAccessor` explicit members after the `State` property (line 21):

```csharp
    // IBoundaryEventStateAccessor
    WorkflowInstanceState IBoundaryEventStateAccessor.State => State;
    IGrainFactory IBoundaryEventStateAccessor.GrainFactory => _grainFactory;
    ILogger IBoundaryEventStateAccessor.Logger => _logger;
    IWorkflowExecutionContext IBoundaryEventStateAccessor.WorkflowExecutionContext => this;

    async Task IBoundaryEventStateAccessor.TransitionToNextActivity() => await TransitionToNextActivity();
    async Task IBoundaryEventStateAccessor.ExecuteWorkflow() => await ExecuteWorkflow();
```

**2c. Replace HandleBoundaryTimerFired (lines 124-157) with delegation:**

Replace the entire `HandleBoundaryTimerFired` method with:

```csharp
    private Task HandleBoundaryTimerFired(BoundaryTimerEvent boundaryTimer, Guid hostActivityInstanceId)
        => _boundaryHandler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostActivityInstanceId);
```

**2d. Replace HandleBoundaryMessageFired body (lines 719-763) with delegation:**

Replace the body of the public `HandleBoundaryMessageFired` method with:

```csharp
    public async Task HandleBoundaryMessageFired(string boundaryActivityId, Guid hostActivityInstanceId)
    {
        await EnsureWorkflowDefinitionAsync();
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();

        var definition = await GetWorkflowDefinition();
        var boundaryMessage = definition.GetActivity(boundaryActivityId) as MessageBoundaryEvent
            ?? throw new InvalidOperationException($"Activity '{boundaryActivityId}' is not a MessageBoundaryEvent");

        await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId);
        await _state.WriteStateAsync();
    }
```

**2e. Replace FailActivityWithBoundaryCheck (lines 531-568) with delegation:**

Replace `FailActivityWithBoundaryCheck` with:

```csharp
    private async Task FailActivityWithBoundaryCheck(string activityId, Exception exception)
    {
        await FailActivityState(activityId, exception);

        // Check for boundary error event
        var definition = await GetWorkflowDefinition();
        var activityEntry = State.GetFirstActive(activityId) ?? State.Entries.Last(e => e.ActivityId == activityId);
        var activityGrain = _grainFactory.GetGrain<IActivityInstanceGrain>(activityEntry.ActivityInstanceId);
        var errorState = await activityGrain.GetErrorState();

        if (errorState is not null)
        {
            var boundaryEvent = definition.Activities
                .OfType<BoundaryErrorEvent>()
                .FirstOrDefault(b => b.AttachedToActivityId == activityId
                    && (b.ErrorCode == null || b.ErrorCode == errorState.Code.ToString()));

            if (boundaryEvent is not null)
            {
                await _boundaryHandler.HandleBoundaryErrorAsync(activityId, boundaryEvent, activityEntry.ActivityInstanceId);
                return;
            }
        }

        await ExecuteWorkflow();
    }
```

**2f. Replace CompleteActivityState cleanup (lines 217-221) with delegation:**

In `CompleteActivityState`, replace lines 217-221:

```csharp
        // Unregister any boundary timer reminders attached to this activity
        await UnregisterBoundaryTimerReminders(activityId, entry.ActivityInstanceId);

        // Unsubscribe any boundary message subscriptions attached to this activity
        await UnsubscribeBoundaryMessageSubscriptions(activityId);
```

with:

```csharp
        // Clean up boundary subscriptions attached to this activity
        await _boundaryHandler.UnregisterBoundaryTimerRemindersAsync(activityId, entry.ActivityInstanceId);
        await _boundaryHandler.UnsubscribeBoundaryMessageSubscriptionsAsync(activityId);
```

**2g. Remove the old private methods** that are now in BoundaryEventHandler:

Delete these methods from WorkflowInstance:
- `UnregisterBoundaryTimerReminders` (lines 224-236)
- `UnsubscribeBoundaryMessageSubscriptions` (lines 238-255)

And remove these `[LoggerMessage]` methods that moved to BoundaryEventHandler:
- `LogBoundaryTimerInterrupted` (EventId 1020)
- `LogBoundaryMessageInterrupted` (EventId 1022)
- `LogBoundaryEventTriggered` (EventId 1016)
- `LogTimerReminderUnregistered` (EventId 1019)

Keep all other LoggerMessage methods in WorkflowInstance.

**Step 3: Build to verify compilation**

Run: `dotnet build src/Fleans/`
Expected: SUCCESS

**Step 4: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS (285 tests). Critical: boundary timer, message boundary, and error boundary integration tests must still pass.

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/ApplicationDependencyInjection.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs
git commit -m "refactor: wire BoundaryEventHandler into WorkflowInstance, delegate boundary calls"
```

---

### Task 4: Write unit tests for BoundaryEventHandler

**Files:**
- Create: `src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs`

**Step 1: Write unit tests**

These tests use NSubstitute to mock `IBoundaryEventStateAccessor`, verifying the handler logic in isolation. Note: since the integration tests already cover the full flow via TestCluster, these unit tests focus on the handler's specific behaviors: stale checks, cleanup logic, and instance creation.

Create `src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs`:

```csharp
using Fleans.Application.Grains;
using Fleans.Application.Services;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;

namespace Fleans.Application.Tests;

[TestClass]
public class BoundaryEventHandlerTests
{
    private IBoundaryEventStateAccessor _accessor = null!;
    private BoundaryEventHandler _handler = null!;
    private IGrainFactory _grainFactory = null!;
    private WorkflowInstanceState _state = null!;

    [TestInitialize]
    public void Setup()
    {
        _accessor = Substitute.For<IBoundaryEventStateAccessor>();
        _grainFactory = Substitute.For<IGrainFactory>();
        _state = new WorkflowInstanceState { Id = Guid.NewGuid() };

        _accessor.State.Returns(_state);
        _accessor.GrainFactory.Returns(_grainFactory);
        _accessor.Logger.Returns(NullLogger.Instance);
        _accessor.TransitionToNextActivity().Returns(Task.CompletedTask);
        _accessor.ExecuteWorkflow().Returns(Task.CompletedTask);

        _handler = new BoundaryEventHandler();
        _handler.Initialize(_accessor);
    }

    [TestMethod]
    public async Task HandleBoundaryTimerFired_StaleActivity_ShouldReturnWithoutAction()
    {
        // Arrange — no matching active entry
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var hostInstanceId = Guid.NewGuid();

        // State has no entries → stale
        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId);

        // Assert — no interactions with grain factory (no instance creation)
        _grainFactory.DidNotReceive().GetGrain<IActivityInstanceGrain>(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task HandleBoundaryMessageFired_StaleActivity_ShouldReturnWithoutAction()
    {
        // Arrange
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1");
        var hostInstanceId = Guid.NewGuid();

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryMsg });
        _accessor.GetWorkflowDefinition().Returns(ValueTask.FromResult(definition));

        // Act
        await _handler.HandleBoundaryMessageFiredAsync(boundaryMsg, hostInstanceId);

        // Assert
        await _accessor.DidNotReceive().TransitionToNextActivity();
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Fleans/Fleans.Application.Tests/ --filter "FullyQualifiedName~BoundaryEventHandlerTests"`
Expected: 2 PASSED

**Step 3: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/BoundaryEventHandlerTests.cs
git commit -m "test: add unit tests for BoundaryEventHandler stale activity checks"
```

---

### Task 5: Final verification

**Step 1: Run full test suite**

Run: `dotnet test src/Fleans/`
Expected: ALL PASS

**Step 2: Verify WorkflowInstance is smaller**

Check that WorkflowInstance no longer contains:
- `UnregisterBoundaryTimerReminders` method
- `UnsubscribeBoundaryMessageSubscriptions` method
- The long `HandleBoundaryTimerFired` body (now a one-liner delegation)
- The long `HandleBoundaryMessageFired` body (now thin wrapper + delegation)
- `LogBoundaryTimerInterrupted`, `LogBoundaryMessageInterrupted`, `LogBoundaryEventTriggered`, `LogTimerReminderUnregistered` (moved to handler)

**Step 3: Verify BoundaryEventHandler contains the extracted logic**

Check that `BoundaryEventHandler` has:
- `HandleBoundaryTimerFiredAsync` — interrupt + unsubscribe messages + create/execute
- `HandleBoundaryMessageFiredAsync` — interrupt + unregister timers + unsubscribe messages (skip fired) + create/execute
- `HandleBoundaryErrorAsync` — create/execute boundary error instance
- `UnregisterBoundaryTimerRemindersAsync` — cancel timer callbacks
- `UnsubscribeBoundaryMessageSubscriptionsAsync` — unsubscribe message correlations
- `CreateAndExecuteBoundaryInstanceAsync` — shared pattern for creating and executing boundary instances
