namespace Fleans.Application.Projections;

/// <summary>
/// The read-model projector of one workflow instance, keyed by the instance id ("D" format). It is
/// driven only by its <see cref="Fleans.Domain.Events.InstanceAdvanced"/> stream.
/// </summary>
public interface IWorkflowInstanceProjector : IGrainWithStringKey
{
}
