using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class EnvironmentVariablesGrain : Grain, IEnvironmentVariablesGrain
{
    private readonly IPersistentState<EnvironmentVariablesState> _state;
    private readonly ILogger<EnvironmentVariablesGrain> _logger;

    private EnvironmentVariablesState State => _state.State;

    public EnvironmentVariablesGrain(
        [PersistentState("state", GrainStorageNames.EnvironmentVariables)]
        IPersistentState<EnvironmentVariablesState> state,
        ILogger<EnvironmentVariablesGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public ValueTask<List<EnvironmentVariableEntry>> GetAll()
        => ValueTask.FromResult(State.Variables.ToList());

    public async ValueTask Set(EnvironmentVariableEntry variable)
    {
        var error = variable.Validate();
        if (error is not null)
            throw new ArgumentException(error);

        var existing = State.Variables.FindIndex(v => v.Name == variable.Name);
        if (existing >= 0)
        {
            var updated = new EnvironmentVariableEntry
            {
                Id = State.Variables[existing].Id,
                Name = variable.Name,
                Value = variable.Value,
                ValueType = variable.ValueType,
                IsSecret = variable.IsSecret,
                ProcessKeys = variable.ProcessKeys
            };
            State.Variables[existing] = updated;
        }
        else
            State.Variables.Add(variable);

        LogVariableSet(variable.Name, variable.ValueType, variable.IsSecret);
        await _state.WriteStateAsync();
    }

    public async ValueTask Remove(string name)
    {
        var removed = State.Variables.RemoveAll(v => v.Name == name);
        if (removed == 0) return;

        LogVariableRemoved(name);
        await _state.WriteStateAsync();
    }

    public ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey)
    {
        var result = new Dictionary<string, object>();
        foreach (var v in State.Variables)
        {
            if (v.ProcessKeys is null || v.ProcessKeys.Contains(processDefinitionKey))
                result[v.Name] = v.GetTypedValue();
        }
        return ValueTask.FromResult(result);
    }

    public ValueTask<ProcessEnvironmentResult> GetEnvironmentForProcess(string processDefinitionKey)
    {
        var variables = new Dictionary<string, object>();
        var secretKeys = new List<string>();
        foreach (var v in State.Variables)
        {
            if (v.ProcessKeys is null || v.ProcessKeys.Contains(processDefinitionKey))
            {
                variables[v.Name] = v.GetTypedValue();
                if (v.IsSecret)
                    secretKeys.Add(v.Name);
            }
        }
        return ValueTask.FromResult(new ProcessEnvironmentResult(variables, secretKeys));
    }

    [LoggerMessage(Level = LogLevel.Information, EventId = 8000,
        Message = "Environment variable '{Name}' set (type={ValueType}, secret={IsSecret})")]
    private partial void LogVariableSet(string name, string valueType, bool isSecret);

    [LoggerMessage(Level = LogLevel.Information, EventId = 8001,
        Message = "Environment variable '{Name}' removed")]
    private partial void LogVariableRemoved(string name);
}
