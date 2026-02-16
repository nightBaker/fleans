using Fleans.Domain;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class ProcessDefinitionGrain : Grain, IProcessDefinitionGrain
{
    private readonly IPersistentState<ProcessDefinition> _state;
    private readonly ILogger<ProcessDefinitionGrain> _logger;

    public ProcessDefinitionGrain(
        [PersistentState("state", "processDefinitions")] IPersistentState<ProcessDefinition> state,
        ILogger<ProcessDefinitionGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public Task<WorkflowDefinition> GetDefinition()
    {
        if (!_state.RecordExists)
        {
            LogDefinitionNotFound(this.GetPrimaryKeyString());
            throw new KeyNotFoundException(
                $"Process definition '{this.GetPrimaryKeyString()}' not found.");
        }

        return Task.FromResult(_state.State.Workflow);
    }

    [LoggerMessage(EventId = 7000, Level = LogLevel.Warning,
        Message = "Process definition '{ProcessDefinitionId}' not found in storage")]
    private partial void LogDefinitionNotFound(string processDefinitionId);
}
