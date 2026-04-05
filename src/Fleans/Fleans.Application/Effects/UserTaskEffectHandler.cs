using Fleans.Application.Grains;
using Fleans.Domain.Effects;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Effects;

public partial class UserTaskEffectHandler : IEffectHandler
{
    private readonly ILogger<UserTaskEffectHandler> _logger;

    public UserTaskEffectHandler(ILogger<UserTaskEffectHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(IInfrastructureEffect effect) =>
        effect is RegisterUserTaskEffect or CompleteUserTaskPersistenceEffect or UpdateUserTaskClaimEffect;

    public async Task HandleAsync(IInfrastructureEffect effect, IEffectContext context)
    {
        switch (effect)
        {
            case RegisterUserTaskEffect regTask:
                try
                {
                    var taskGrain = context.GrainFactory.GetGrain<IUserTaskGrain>(regTask.ActivityInstanceId);
                    await taskGrain.Register(
                        regTask.WorkflowInstanceId, regTask.ActivityId,
                        regTask.Assignee, regTask.CandidateGroups, regTask.CandidateUsers,
                        regTask.ExpectedOutputVariables);
                }
                catch (Exception ex)
                {
                    LogUserTaskRegistrationFailed(regTask.ActivityInstanceId, ex);
                }
                break;

            case CompleteUserTaskPersistenceEffect completeTask:
                try
                {
                    var completeGrain = context.GrainFactory.GetGrain<IUserTaskGrain>(completeTask.ActivityInstanceId);
                    await completeGrain.MarkCompleted();
                }
                catch (Exception ex)
                {
                    LogUserTaskCompletionPersistenceFailed(completeTask.ActivityInstanceId, ex);
                }
                break;

            case UpdateUserTaskClaimEffect claimUpdate:
                try
                {
                    var claimGrain = context.GrainFactory.GetGrain<IUserTaskGrain>(claimUpdate.ActivityInstanceId);
                    await claimGrain.UpdateClaim(claimUpdate.ClaimedBy, claimUpdate.TaskState);
                }
                catch (Exception ex)
                {
                    LogUserTaskClaimUpdateFailed(claimUpdate.ActivityInstanceId, ex);
                }
                break;

            default:
                throw new InvalidOperationException($"Unexpected effect type in {nameof(UserTaskEffectHandler)}: {effect.GetType().Name}");
        }
    }

    [LoggerMessage(EventId = 1063, Level = LogLevel.Warning,
        Message = "User task registration persistence failed: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskRegistrationFailed(Guid activityInstanceId, Exception exception);

    [LoggerMessage(EventId = 1064, Level = LogLevel.Warning,
        Message = "User task completion persistence failed: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskCompletionPersistenceFailed(Guid activityInstanceId, Exception exception);

    [LoggerMessage(EventId = 1065, Level = LogLevel.Warning,
        Message = "User task claim update persistence failed: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskClaimUpdateFailed(Guid activityInstanceId, Exception exception);
}
