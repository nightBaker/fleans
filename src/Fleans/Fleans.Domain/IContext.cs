namespace Fleans.Domain;

public interface IContext
{
    Guid InstanceId { get; }
    
    IReadOnlyDictionary<string, object> Variables { get; }
}