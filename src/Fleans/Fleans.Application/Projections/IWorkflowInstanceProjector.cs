namespace Fleans.Application.Projections;

/// <summary>
/// The read-model projector of one bucket of workflow instances, keyed by the bucket number
/// (<see cref="ProjectorOptions.BucketFor"/>, #1011). It is driven only by its bucket's
/// <see cref="Fleans.Domain.Events.InstanceAdvanced"/> stream.
/// </summary>
public interface IWorkflowInstanceProjector : IGrainWithStringKey
{
}
