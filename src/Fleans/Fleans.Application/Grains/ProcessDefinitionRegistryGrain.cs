using Fleans.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

public partial class ProcessDefinitionRegistryGrain : Grain, IProcessDefinitionRegistryGrain
{
    private readonly ILogger<ProcessDefinitionRegistryGrain> _logger;
    private readonly IProcessDefinitionRepository _repository;
    private readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal);

    public ProcessDefinitionRegistryGrain(
        ILogger<ProcessDefinitionRegistryGrain> logger,
        IProcessDefinitionRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        var keys = await _repository.GetAllDistinctKeysAsync();
        foreach (var key in keys)
            _knownKeys.Add(key);
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
}
