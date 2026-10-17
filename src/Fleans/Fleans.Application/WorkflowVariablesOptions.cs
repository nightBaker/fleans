namespace Fleans.Application;

/// <summary>
/// Limits on workflow variables, bound from <c>Fleans:Variables</c>.
/// </summary>
public sealed class WorkflowVariablesOptions
{
    public const string SectionName = "Fleans:Variables";

    /// <summary>
    /// The largest variables document, in bytes of its JSON form, that one operation may hand a
    /// workflow instance: start variables, an activity's completion variables (scripts, workers,
    /// REST), a user-task completion and a message delivery. A larger one is rejected with an
    /// <see cref="ArgumentException"/> before anything is applied — REST callers get 400, a script
    /// or custom task fails its activity. An activity failure still lands, without its oversized
    /// variables. Variables moved inside the engine (child output, joins, compensation) were
    /// already checked on the way in. Default 1 MiB.
    /// </summary>
    public int MaxSizeBytes { get; set; } = 1024 * 1024;
}
