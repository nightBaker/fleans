using Fleans.Domain.Activities;
using Fleans.Domain.Connections;

namespace Fleans.Domain.Builders;

public record ActivityBuilderResult
{
    public required IActivity Activity { get; init; }
    public required IEnumerable<IActivity> ChildActivities { get; init; }
    public required IEnumerable<IWorkflowConnection<IActivity, IActivity>> Connections { get; init; }

}