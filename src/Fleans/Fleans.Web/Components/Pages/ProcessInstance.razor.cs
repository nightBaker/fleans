using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class ProcessInstance
{
    [Inject]
    private ILogger<ProcessInstance> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13040, Level = LogLevel.Error, Message = "Error loading instance {InstanceId}")]
    private partial void LogLoadInstanceError(Guid instanceId, Exception ex);

    [LoggerMessage(EventId = 13041, Level = LogLevel.Warning, Message = "Failed to load compensation log for instance {InstanceId}")]
    private partial void LogLoadCompensationLogFailed(Guid instanceId, Exception ex);

    [LoggerMessage(EventId = 13042, Level = LogLevel.Debug, Message = "Journal replay stopped for instance {InstanceId}")]
    private partial void LogJournalReplayStopped(Guid instanceId, Exception ex);

    [LoggerMessage(EventId = 13043, Level = LogLevel.Error, Message = "Error refreshing instance {InstanceId}")]
    private partial void LogRefreshInstanceError(Guid instanceId, Exception ex);
}
