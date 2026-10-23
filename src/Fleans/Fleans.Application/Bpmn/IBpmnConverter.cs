using Fleans.Domain;

namespace Fleans.Application.Bpmn;

/// <summary>
/// Parses BPMN 2.0 XML into a <see cref="WorkflowDefinition"/>. Implemented by
/// <c>Fleans.Infrastructure.Bpmn.BpmnConverter</c>; the port lives here so silo-side grains
/// (the deployment-request grain deploys stored XML, #900) can parse without referencing
/// Infrastructure.
/// </summary>
public interface IBpmnConverter
{
    Task<WorkflowDefinition> ConvertFromXmlAsync(Stream bpmnXmlStream);
}
