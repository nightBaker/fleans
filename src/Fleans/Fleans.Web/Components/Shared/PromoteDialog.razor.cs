using Microsoft.AspNetCore.Components;

namespace Fleans.Web.Components.Shared;

public partial class PromoteDialog
{
    [Inject] private ILogger<PromoteDialog> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13005, Level = LogLevel.Error, Message = "Error promoting process {ProcessDefinitionKey} to environment {Target}")]
    private partial void LogPromoteError(string processDefinitionKey, string target, Exception ex);
}
