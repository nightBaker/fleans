namespace Fleans.Domain;

/// <summary>
/// Named grain storage provider identifiers used in [PersistentState] attributes
/// and DI registration. Must match between grain constructors and silo configuration.
/// </summary>
public static class GrainStorageNames
{
    public const string ProcessDefinitions = "processDefinitions";
    public const string TimerSchedulers = "timerSchedulers";
    public const string MessageCorrelations = "messageCorrelations";
    public const string SignalCorrelations = "signalCorrelations";
    public const string MessageStartEventListeners = "messageStartEventListeners";
    public const string SignalStartEventListeners = "signalStartEventListeners";
    public const string UserTasks = "userTasks";
    public const string ConditionalStartEventListeners = "conditionalStartEventListeners";
    public const string ConditionalStartEventRegistry = "conditionalStartEventRegistry";
}
