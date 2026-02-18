namespace Fleans.Domain;

/// <summary>
/// Named grain storage provider identifiers used in [PersistentState] attributes
/// and DI registration. Must match between grain constructors and silo configuration.
/// </summary>
public static class GrainStorageNames
{
    public const string ActivityInstances = "activityInstances";
    public const string WorkflowInstances = "workflowInstances";
    public const string ProcessDefinitions = "processDefinitions";
    public const string TimerSchedulers = "timerSchedulers";
    public const string MessageCorrelations = "messageCorrelations";
}
