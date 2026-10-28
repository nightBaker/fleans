using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class Deployments
{
    [Inject]
    private ILogger<Deployments> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13060, Level = LogLevel.Error, Message = "Failed to load deployment requests")]
    private partial void LogLoadDeploymentRequestsFailed(Exception ex);

    [LoggerMessage(EventId = 13061, Level = LogLevel.Warning, Message = "Deployment request action failed")]
    private partial void LogDeploymentRequestActionFailed(Exception ex);
}
