using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class Editor
{
    [Inject]
    private ILogger<Editor> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13020, Level = LogLevel.Error, Message = "Failed to import BPMN file")]
    private partial void LogImportFailed(Exception ex);

    [LoggerMessage(EventId = 13021, Level = LogLevel.Error, Message = "Failed to download XML")]
    private partial void LogDownloadFailed(Exception ex);

    [LoggerMessage(EventId = 13022, Level = LogLevel.Error, Message = "Failed to prepare deployment")]
    private partial void LogPrepareDeploymentFailed(Exception ex);

    [LoggerMessage(EventId = 13023, Level = LogLevel.Error, Message = "Failed to deploy workflow")]
    private partial void LogDeployFailed(Exception ex);

    [LoggerMessage(EventId = 13024, Level = LogLevel.Error, Message = "Failed to request deployment")]
    private partial void LogRequestDeploymentFailed(Exception ex);

    [LoggerMessage(EventId = 13025, Level = LogLevel.Error, Message = "Failed to load BPMN for key {Key}")]
    private partial void LogLoadBpmnForKeyFailed(string key, Exception ex);

    [LoggerMessage(EventId = 13026, Level = LogLevel.Error, Message = "Failed to load BPMN for tab {TabId}")]
    private partial void LogLoadBpmnForTabFailed(string tabId, Exception ex);

    [LoggerMessage(EventId = 13027, Level = LogLevel.Warning, Message = "Failed to snapshot XML for active tab")]
    private partial void LogSnapshotXmlFailed(Exception ex);

    [LoggerMessage(EventId = 13028, Level = LogLevel.Debug, Message = "localStorage write for editor tabs was rejected (quota?)")]
    private partial void LogTabsWriteRejected();

    [LoggerMessage(EventId = 13029, Level = LogLevel.Warning, Message = "Failed to persist editor tabs")]
    private partial void LogPersistTabsFailed(Exception ex);

    [LoggerMessage(EventId = 13030, Level = LogLevel.Warning, Message = "Failed to restore editor tabs from localStorage")]
    private partial void LogRestoreTabsFailed(Exception ex);

    [LoggerMessage(EventId = 13031, Level = LogLevel.Debug, Message = "setDirtyTabsFlag interop failed")]
    private partial void LogDirtyTabsFlagInteropFailed(Exception ex);

    [LoggerMessage(EventId = 13032, Level = LogLevel.Error, Message = "Failed to start the simulation")]
    private partial void LogSimulationStartFailed(Exception ex);

    [LoggerMessage(EventId = 13033, Level = LogLevel.Error, Message = "Failed to load versions for {Key}")]
    private partial void LogLoadVersionsFailed(string? key, Exception ex);

    [LoggerMessage(EventId = 13034, Level = LogLevel.Error, Message = "Failed to compare with {Key} v{Version}")]
    private partial void LogCompareFailed(string? key, int version, Exception ex);

    [LoggerMessage(EventId = 13035, Level = LogLevel.Debug, Message = "Editor presence heartbeat failed")]
    private partial void LogPresenceHeartbeatFailed(Exception ex);

    [LoggerMessage(EventId = 13036, Level = LogLevel.Debug, Message = "Editor presence leave failed")]
    private partial void LogPresenceLeaveFailed(Exception ex);
}
