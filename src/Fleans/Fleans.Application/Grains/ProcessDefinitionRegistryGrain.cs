using Fleans.Application.Placement;
using Fleans.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

[Reentrant]
[CorePlacement]
public partial class ProcessDefinitionRegistryGrain : Grain, IProcessDefinitionRegistryGrain
{
    private readonly IProcessDefinitionRepository _repository;
    private readonly ILogger<ProcessDefinitionRegistryGrain> _logger;
    private readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal);

    public ProcessDefinitionRegistryGrain(
        IProcessDefinitionRepository repository,
        ILogger<ProcessDefinitionRegistryGrain> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        var keys = await _repository.GetAllDistinctKeysAsync();
        foreach (var key in keys)
            _knownKeys.Add(key);

        LogRegistryActivated(_knownKeys.Count);
    }

    public Task RegisterKey(string processDefinitionKey)
    {
        if (_knownKeys.Add(processDefinitionKey))
            LogKeyRegistered(processDefinitionKey);

        return Task.CompletedTask;
    }

    public Task<List<string>> GetAllKeys()
    {
        return Task.FromResult(_knownKeys.ToList());
    }

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information, Message = "Registered process definition key '{ProcessDefinitionKey}'")]
    private partial void LogKeyRegistered(string processDefinitionKey);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information, Message = "ProcessDefinitionRegistryGrain activated with {KeyCount} known key(s)")]
    private partial void LogRegistryActivated(int keyCount);
}
