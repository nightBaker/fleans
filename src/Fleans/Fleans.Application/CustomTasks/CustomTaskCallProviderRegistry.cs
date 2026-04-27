namespace Fleans.Application.CustomTasks;

public sealed class CustomTaskCallProviderRegistry
{
    private readonly Dictionary<string, Type> _byType;

    public CustomTaskCallProviderRegistry(IEnumerable<CustomTaskRegistration> registrations)
    {
        _byType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registrations)
        {
            if (_byType.TryGetValue(r.TaskType, out var existing))
            {
                throw new InvalidOperationException(
                    $"Duplicate custom-task registration for type '{r.TaskType}': {existing.FullName} and {r.GrainInterface.FullName}");
            }
            _byType[r.TaskType] = r.GrainInterface;
        }
    }

    public bool TryGetGrainInterface(string taskType, out Type? grainInterface)
        => _byType.TryGetValue(taskType, out grainInterface);
}
