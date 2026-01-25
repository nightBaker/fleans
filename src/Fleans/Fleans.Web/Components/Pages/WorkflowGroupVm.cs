using System.Collections.Generic;
using Fleans.Domain;

namespace Fleans.Web.Components.Pages;

public sealed class WorkflowGroupVm
{
    public required string ProcessDefinitionKey { get; init; }
    public required int LatestVersion { get; init; }
    public required int TotalVersions { get; init; }
    public required List<ProcessDefinitionSummary> Versions { get; init; }
}
