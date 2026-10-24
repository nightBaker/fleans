using Orleans.Configuration;

namespace Fleans.Application.Projections;

/// <summary>
/// The grain set of a <c>Fleans:Role=Projector</c> silo (#861): the read-model projector and
/// nothing else of Fleans.
/// </summary>
/// <remarks>
/// <see cref="Placement.ProjectorPlacementAttribute"/> only decides where the projector goes. Without
/// this, grains with Orleans' default placement (custom-task handlers) could still activate on a
/// projector silo, because every grain class the image carries is compatible there. Removing the
/// classes from <see cref="GrainTypeOptions"/> takes them out of the silo's manifest, so
/// <c>GetCompatibleSilos</c> never offers the silo for them. Orleans' own grains (management,
/// dashboard, streaming) stay.
/// </remarks>
public static class ProjectorSilo
{
    /// <summary>Removes every Fleans grain class except <see cref="WorkflowInstanceProjector"/>.</summary>
    public static void KeepOnlyProjectorGrains(GrainTypeOptions options) =>
        options.Classes.RemoveWhere(type => type != typeof(WorkflowInstanceProjector) && IsFleansType(type));

    private static bool IsFleansType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Fleans.", StringComparison.Ordinal) == true;
}
