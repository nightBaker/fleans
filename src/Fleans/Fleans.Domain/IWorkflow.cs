namespace Fleans.Domain;

public interface IWorkflow
{
    string Name { get; }
    string Version { get; }
    IContext Context { get; }
    
    Guid Start();
    void Cancel();
    void CompleteCurrentActivity();
}
