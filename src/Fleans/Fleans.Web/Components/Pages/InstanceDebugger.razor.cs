using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class InstanceDebugger
{
    [Inject]
    private ILogger<InstanceDebugger> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13055, Level = LogLevel.Warning, Message = "Failed to read the journal of instance {InstanceId}")]
    private partial void LogReadJournalFailed(Guid instanceId, Exception ex);
}
