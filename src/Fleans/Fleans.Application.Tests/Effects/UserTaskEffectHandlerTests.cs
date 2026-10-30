using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;

namespace Fleans.Application.Tests.Effects;

[TestClass]
public class UserTaskEffectHandlerTests
{
    private IEffectContext _context = null!;
    private IUserTaskGrain _task = null!;
    private RegisterUserTaskEffect _effect = null!;
    private UserTaskEffectHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        _context = Substitute.For<IEffectContext>();
        var factory = Substitute.For<IGrainFactory>();
        _task = Substitute.For<IUserTaskGrain>();
        _effect = new RegisterUserTaskEffect(Guid.NewGuid(), Guid.NewGuid(), "approval",
            "alice", new List<string> { "managers" }, new List<string> { "bob" },
            new List<string> { "approved" });
        factory.GetGrain<IUserTaskGrain>(_effect.ActivityInstanceId, null).Returns(_task);
        _context.GrainFactory.Returns(factory);
        _handler = new UserTaskEffectHandler(NullLogger<UserTaskEffectHandler>.Instance);
    }

    [TestMethod]
    public async Task SuccessfulRegistration_DoesNotFailActivity()
    {
        await _handler.HandleAsync(_effect, _context);

        await Register(_task.Received(1));
        await _context.DidNotReceiveWithAnyArgs().ProcessFailureEffects(default!, default, default!);
    }

    [TestMethod]
    public async Task RegistrationRecoversOnThirdAttempt_DoesNotFailActivity()
    {
        var attempts = 0;
        Register(_task).Returns(_ => ++attempts < 3
            ? Task.FromException(new InvalidOperationException("storage unavailable"))
            : Task.CompletedTask);

        await _handler.HandleAsync(_effect, _context);

        await Register(_task.Received(3));
        await _context.DidNotReceiveWithAnyArgs().ProcessFailureEffects(default!, default, default!);
    }

    [TestMethod]
    public async Task RegistrationKeepsFailing_FailsTheExactActivityInstanceAfterThreeAttempts()
    {
        var failure = new InvalidOperationException("storage unavailable");
        Register(_task).Returns(_ => Task.FromException(failure));

        await _handler.HandleAsync(_effect, _context);

        await Register(_task.Received(3));
        await _context.Received(1).ProcessFailureEffects(_effect.ActivityId, _effect.ActivityInstanceId, failure);
    }

    [TestMethod]
    public async Task FailureHandlingThrows_PropagatesWithoutRetryingRegistration()
    {
        Register(_task).Returns(_ => Task.FromException(new InvalidOperationException("storage unavailable")));
        var failure = new InvalidOperationException("failure handling unavailable");
        _context.ProcessFailureEffects(_effect.ActivityId, _effect.ActivityInstanceId, Arg.Any<Exception>())
            .Returns(_ => Task.FromException(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.HandleAsync(_effect, _context));

        Assert.AreSame(failure, thrown);
        await Register(_task.Received(3));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CompletionOrClaimPersistenceFails_DoesNotFailActivity(bool completing)
    {
        _task.MarkCompleted().Returns(_ => Task.FromException(new InvalidOperationException("write failed")));
        _task.UpdateClaim("alice", UserTaskLifecycleState.Claimed)
            .Returns(_ => Task.FromException(new InvalidOperationException("write failed")));
        IInfrastructureEffect effect = completing
            ? new CompleteUserTaskPersistenceEffect(_effect.ActivityInstanceId)
            : new UpdateUserTaskClaimEffect(_effect.ActivityInstanceId, "alice", UserTaskLifecycleState.Claimed);

        await _handler.HandleAsync(effect, _context);

        await _context.DidNotReceiveWithAnyArgs().ProcessFailureEffects(default!, default, default!);
    }

    private Task Register(IUserTaskGrain grain) => grain.Register(
        _effect.WorkflowInstanceId, _effect.ActivityId, _effect.Assignee,
        _effect.CandidateGroups, _effect.CandidateUsers, _effect.ExpectedOutputVariables);
}
