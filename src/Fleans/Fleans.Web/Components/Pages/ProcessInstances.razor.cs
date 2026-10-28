using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class ProcessInstances
{
    [Inject]
    private ILogger<ProcessInstances> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13050, Level = LogLevel.Error, Message = "Error loading instances for {Key}")]
    private partial void LogLoadInstancesError(string key, Exception ex);
}
