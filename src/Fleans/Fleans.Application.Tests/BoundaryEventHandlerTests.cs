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
}
