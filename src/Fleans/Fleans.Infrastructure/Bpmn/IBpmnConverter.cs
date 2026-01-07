using Fleans.Domain;

namespace Fleans.Infrastructure.Bpmn;

public interface IBpmnConverter
{
    Task<WorkflowDefinition> ConvertFromXmlAsync(Stream bpmnXmlStream);
}