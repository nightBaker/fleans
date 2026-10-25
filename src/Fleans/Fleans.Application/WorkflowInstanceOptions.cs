namespace Fleans.Application;

/// <summary>
/// How long a <c>WorkflowInstance</c> activation stays in memory while nothing calls it
/// (<c>Fleans:Instances</c>).
/// </summary>
public sealed class WorkflowInstanceOptions
{
    public const string SectionName = "Fleans:Instances";

    /// <summary>
    /// Idle time after which Orleans collects a <c>WorkflowInstance</c> activation
    /// (<c>Fleans:Instances:IdleCollectionAge</c>, default 2 minutes; Orleans' default for every grain
    /// is 15). An instance that waits — for a timer, a message, a user task — otherwise holds its
    /// activation for the full 15 minutes: ~23 KB each on the load stack, so 60 000 waiting instances
    /// filled three 1 GB Core silos. The journal is the state, so a collected instance reactivates from
    /// its snapshot and journal tail when its timer fires or its message arrives. Must be longer than
    /// Orleans' collection quantum (1 minute).
    /// </summary>
    public TimeSpan IdleCollectionAge { get; set; } = TimeSpan.FromMinutes(2);
}
