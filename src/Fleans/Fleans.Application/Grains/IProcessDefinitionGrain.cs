using Fleans.Domain;

namespace Fleans.Application.Grains;

public interface IProcessDefinitionGrain : IGrainWithStringKey
{
    Task<WorkflowDefinition> GetDefinition();
}
