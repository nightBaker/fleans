using Fleans.Domain;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IActivityInstanceGrain : IGrainWithGuidKey, IActivityExecutionContext
{
    // Re-declare inherited read-only methods with [ReadOnly] for Orleans concurrency optimization
    [ReadOnly]
    new ValueTask<Guid> GetActivityInstanceId();

    [ReadOnly]
    new ValueTask<string> GetActivityId();

    [ReadOnly]
    new ValueTask<bool> IsCompleted();

    [ReadOnly]
    ValueTask<bool> IsExecuting();

    [ReadOnly]
    ValueTask<Guid> GetVariablesStateId();

    ValueTask Fail(Exception exception);
    ValueTask SetActivity(string activityId, string activityType);
    ValueTask SetVariablesId(Guid guid);
}
