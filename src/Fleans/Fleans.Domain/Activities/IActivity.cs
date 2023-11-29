namespace Fleans.Domain.Activities;

public interface IActivity
{
    Guid Id { get; }
}

public interface IExecutableActivity: IActivity
{
    Task<IActivityExecutionResult> ExecuteAsync(IContext context);
    void Fail(Exception exception);
    bool IsCompleted { get; }
    ActivityStatus Status { get; }
}

public interface IActivity<TResult> : IActivity
{
    
}