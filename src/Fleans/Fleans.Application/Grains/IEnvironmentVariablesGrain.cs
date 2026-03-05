using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IEnvironmentVariablesGrain : IGrainWithIntegerKey
{
    [ReadOnly] ValueTask<List<EnvironmentVariableEntry>> GetAll();
    ValueTask Set(EnvironmentVariableEntry variable);
    ValueTask Remove(string name);
    [ReadOnly] ValueTask<Dictionary<string, object>> GetVariablesForProcess(string processDefinitionKey);
    [ReadOnly] ValueTask<HashSet<string>> GetSecretKeysForProcess(string processDefinitionKey);
}
