using Fleans.Application.Grains;
using Fleans.Application.Services;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
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
        _state = new WorkflowInstanceState();

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
        // Arrange — no matching active entry (state has no entries)
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef);
        var hostInstanceId = Guid.NewGuid();
        var definition = Substitute.For<IWorkflowDefinition>();

        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

        // Assert — no transition or execution happened
        await _accessor.DidNotReceive().TransitionToNextActivity();
        await _accessor.DidNotReceive().ExecuteWorkflow();
    }

    [TestMethod]
    public async Task HandleBoundaryMessageFired_StaleActivity_ShouldReturnWithoutAction()
    {
        // Arrange
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1");
        var hostInstanceId = Guid.NewGuid();

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryMsg });

        // Act
        await _handler.HandleBoundaryMessageFiredAsync(boundaryMsg, hostInstanceId, definition);

        // Assert
        await _accessor.DidNotReceive().TransitionToNextActivity();
        await _accessor.DidNotReceive().ExecuteWorkflow();
    }

    // --- Non-interrupting timer tests ---

    [TestMethod]
    public async Task HandleBoundaryTimerFired_NonInterrupting_ShouldNotCancelAttachedActivity()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryTimer });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

        // Assert — attached activity NOT cancelled
        await attachedGrain.DidNotReceive().Cancel(Arg.Any<string>());
        // Attached entry NOT completed
        Assert.IsFalse(entry.IsCompleted, "Attached entry should NOT be completed for non-interrupting boundary");
        // CancelScopeChildren NOT called
        await _accessor.DidNotReceive().CancelScopeChildren(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task HandleBoundaryTimerFired_NonInterrupting_ShouldCloneVariableScope()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryTimer });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        var variableCountBefore = _state.VariableStates.Count;

        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

        // Assert — variable scope was cloned (new variable state added)
        Assert.AreEqual(variableCountBefore + 1, _state.VariableStates.Count,
            "A cloned variable scope should be added for non-interrupting boundary");

        // The boundary instance should be set with the cloned variables ID (not the original)
        var clonedVariablesId = _state.VariableStates.Last().Id;
        Assert.AreNotEqual(variablesId, clonedVariablesId, "Cloned variable scope should have a new ID");
        await boundaryGrain.Received().SetVariablesId(clonedVariablesId);
    }

    [TestMethod]
    public async Task HandleBoundaryTimerFired_Interrupting_ShouldCancelAttachedActivity()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: true);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryTimer });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

        // Assert — attached activity WAS cancelled
        await attachedGrain.Received().Cancel(Arg.Any<string>());
        Assert.IsTrue(entry.IsCompleted, "Attached entry should be completed for interrupting boundary");
        // No variable cloning for interrupting
        Assert.AreEqual(1, _state.VariableStates.Count, "No cloned variable scope for interrupting boundary");
        await boundaryGrain.Received().SetVariablesId(variablesId);
    }

    // --- Non-interrupting message tests ---

    [TestMethod]
    public async Task HandleBoundaryMessageFired_NonInterrupting_ShouldNotCancelAttachedActivity()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1", IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryMsg });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        // Act
        await _handler.HandleBoundaryMessageFiredAsync(boundaryMsg, hostInstanceId, definition);

        // Assert — attached activity NOT cancelled
        await attachedGrain.DidNotReceive().Cancel(Arg.Any<string>());
        Assert.IsFalse(entry.IsCompleted, "Attached entry should NOT be completed for non-interrupting boundary");
        await _accessor.DidNotReceive().CancelScopeChildren(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task HandleBoundaryMessageFired_NonInterrupting_ShouldCloneVariableScope()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var boundaryMsg = new MessageBoundaryEvent("bm1", "task1", "msg-def-1", IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryMsg });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        var variableCountBefore = _state.VariableStates.Count;

        // Act
        await _handler.HandleBoundaryMessageFiredAsync(boundaryMsg, hostInstanceId, definition);

        // Assert — variable scope was cloned
        Assert.AreEqual(variableCountBefore + 1, _state.VariableStates.Count,
            "A cloned variable scope should be added for non-interrupting boundary");
        var clonedVariablesId = _state.VariableStates.Last().Id;
        Assert.AreNotEqual(variablesId, clonedVariablesId);
        await boundaryGrain.Received().SetVariablesId(clonedVariablesId);
    }

    // --- Non-interrupting signal tests ---

    [TestMethod]
    public async Task HandleBoundarySignalFired_NonInterrupting_ShouldNotCancelAttachedActivity()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var boundarySignal = new SignalBoundaryEvent("bs1", "task1", "sig-def-1", IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundarySignal });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        // Act
        await _handler.HandleBoundarySignalFiredAsync(boundarySignal, hostInstanceId, definition);

        // Assert — attached activity NOT cancelled
        await attachedGrain.DidNotReceive().Cancel(Arg.Any<string>());
        Assert.IsFalse(entry.IsCompleted, "Attached entry should NOT be completed for non-interrupting boundary");
        await _accessor.DidNotReceive().CancelScopeChildren(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task HandleBoundarySignalFired_NonInterrupting_ShouldCloneVariableScope()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var boundarySignal = new SignalBoundaryEvent("bs1", "task1", "sig-def-1", IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundarySignal });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        var variableCountBefore = _state.VariableStates.Count;

        // Act
        await _handler.HandleBoundarySignalFiredAsync(boundarySignal, hostInstanceId, definition);

        // Assert — variable scope was cloned
        Assert.AreEqual(variableCountBefore + 1, _state.VariableStates.Count,
            "A cloned variable scope should be added for non-interrupting boundary");
        var clonedVariablesId = _state.VariableStates.Last().Id;
        Assert.AreNotEqual(variablesId, clonedVariablesId);
        await boundaryGrain.Received().SetVariablesId(clonedVariablesId);
    }

    [TestMethod]
    public async Task HandleBoundarySignalFired_StaleActivity_ShouldReturnWithoutAction()
    {
        // Arrange — no matching active entry
        var boundarySignal = new SignalBoundaryEvent("bs1", "task1", "sig-def-1");
        var hostInstanceId = Guid.NewGuid();

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundarySignal });

        // Act
        await _handler.HandleBoundarySignalFiredAsync(boundarySignal, hostInstanceId, definition);

        // Assert
        await _accessor.DidNotReceive().TransitionToNextActivity();
        await _accessor.DidNotReceive().ExecuteWorkflow();
    }

    // --- Verify boundary path is created for non-interrupting ---

    [TestMethod]
    public async Task HandleBoundaryTimerFired_NonInterrupting_ShouldCreateBoundaryPathAndTransition()
    {
        // Arrange
        var hostInstanceId = Guid.NewGuid();
        var variablesId = Guid.NewGuid();
        var timerDef = new TimerDefinition(TimerType.Duration, "PT10M");
        var boundaryTimer = new BoundaryTimerEvent("bt1", "task1", timerDef, IsInterrupting: false);

        var entry = new ActivityInstanceEntry(hostInstanceId, "task1", _state.Id);
        _state.AddEntries([entry]);
        _state.VariableStates.Add(new WorkflowVariablesState(variablesId, _state.Id));

        var attachedGrain = Substitute.For<IActivityInstanceGrain>();
        attachedGrain.GetVariablesStateId().Returns(new ValueTask<Guid>(variablesId));
        _grainFactory.GetGrain<IActivityInstanceGrain>(hostInstanceId).Returns(attachedGrain);

        var boundaryGrain = Substitute.For<IActivityInstanceGrain>();
        _grainFactory.GetGrain<IActivityInstanceGrain>(Arg.Is<Guid>(id => id != hostInstanceId)).Returns(boundaryGrain);

        var definition = Substitute.For<IWorkflowDefinition>();
        definition.Activities.Returns(new List<Activity> { boundaryTimer });
        definition.SequenceFlows.Returns(new List<SequenceFlow>());

        _accessor.ProcessCommands(Arg.Any<IReadOnlyList<IExecutionCommand>>(), Arg.Any<ActivityInstanceEntry>(), Arg.Any<IActivityExecutionContext>())
            .Returns(Task.CompletedTask);
        _accessor.WorkflowExecutionContext.Returns(Substitute.For<IWorkflowExecutionContext>());

        // Act
        await _handler.HandleBoundaryTimerFiredAsync(boundaryTimer, hostInstanceId, definition);

        // Assert — boundary path created: transition and execution happened
        await _accessor.Received(1).TransitionToNextActivity();
        await _accessor.Received(1).ExecuteWorkflow();
        // A new boundary entry was added
        Assert.AreEqual(2, _state.Entries.Count, "Boundary instance entry should be added");
        var boundaryEntry = _state.Entries.Last();
        Assert.AreEqual("bt1", boundaryEntry.ActivityId);
    }
}
