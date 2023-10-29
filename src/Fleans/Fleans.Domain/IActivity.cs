namespace Fleans.Domain;

public interface IActivity
{
    Task ExecuteAsync(IContext context);
    IActivity[] GetNextActivites(IContext context);
    bool IsCompleted { get; }
}

public interface IActivity<TResult> : IActivity
{
    ActivityResult<TResult>? ExecutionResult { get; }
}