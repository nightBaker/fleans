namespace Fleans.Domain;

public interface IActivity
{
    Guid Id { get; }
    Task<IActivityExecutionResult> ExecuteAsync(IContext context);
    void Fail(Exception exception);
    bool IsCompleted { get; }
    ActivityStatus Status { get; }    
}

public interface IActivity<TResult> : IActivity
{
    
}
