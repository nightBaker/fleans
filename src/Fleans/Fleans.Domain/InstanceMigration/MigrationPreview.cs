using Fleans.Domain.Activities;

namespace Fleans.Domain.InstanceMigration;

/// <summary>
/// How one element of the source version maps by default (identity mapping): to the target
/// element with the same id, when there is one of the same type in the same scope (#1069).
/// </summary>
/// <param name="TargetActivityId">The element with the same id in the target; null when there is none.</param>
/// <param name="Problem">Why the default mapping does not work (a rejection code); null when it does.</param>
[GenerateSerializer]
public sealed record MigrationPreviewElement(
    [property: Id(0)] string SourceActivityId,
    [property: Id(1)] string SourceActivityType,
    [property: Id(2)] string? TargetActivityId,
    [property: Id(3)] string? TargetActivityType,
    [property: Id(4)] string? Problem);

public static class MigrationPreviewBuilder
{
    /// <summary>
    /// Every element of <paramref name="source"/> (nested scopes included) with its identity
    /// mapping into <paramref name="target"/>. Elements that never hold a token (start events,
    /// boundary events) are listed too, so the mapping grid can offer them.
    /// </summary>
    public static List<MigrationPreviewElement> Build(IWorkflowDefinition source, IWorkflowDefinition target)
    {
        var elements = new List<MigrationPreviewElement>();
        foreach (var element in AllElements(source))
        {
            var targetElement = MigrationWaits.FindElement(target, element.ActivityId);
            string? problem = null;
            if (targetElement is null)
                problem = MigrationRejectionCodes.UnmappedActivity;
            else if (element is MultiInstanceActivity != targetElement is MultiInstanceActivity)
                problem = MigrationRejectionCodes.MultiInstanceChanged;
            else if (element.GetType() != targetElement.GetType())
                problem = MigrationRejectionCodes.TypeChanged;
            else if (MigrationWaits.ScopeIdOf(source, element.ActivityId) != MigrationWaits.ScopeIdOf(target, element.ActivityId))
                problem = MigrationRejectionCodes.ScopeChanged;

            elements.Add(new MigrationPreviewElement(element.ActivityId, element.GetType().Name,
                targetElement?.ActivityId, targetElement?.GetType().Name, problem));
        }
        return elements;
    }

    /// <summary>The elements of a definition and of every scope nested in it.</summary>
    public static IEnumerable<Activity> AllElements(IWorkflowDefinition definition)
    {
        foreach (var activity in definition.Activities)
        {
            yield return activity;
            var nested = activity switch
            {
                IWorkflowDefinition scope => scope,
                MultiInstanceActivity { InnerActivity: IWorkflowDefinition inner } => inner,
                _ => null
            };
            if (nested is null)
                continue;
            foreach (var child in AllElements(nested))
                yield return child;
        }
    }
}
