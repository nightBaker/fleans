using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Fleans.Web.Components.Pages;

public partial class Workflows
{
    [Inject]
    private ILogger<Workflows> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13000, Level = LogLevel.Error, Message = "Error loading workflows")]
    private partial void LogLoadWorkflowsError(Exception ex);

    [LoggerMessage(EventId = 13001, Level = LogLevel.Information, Message = "Process definition {ProcessDefinitionId} started successfully with instanceId {WorkflowInstanceId}")]
    private partial void LogProcessStarted(string processDefinitionId, Guid workflowInstanceId);

    [LoggerMessage(EventId = 13002, Level = LogLevel.Warning, Message = "Failed to start process definition {ProcessDefinitionId}: {Error}")]
    private partial void LogProcessStartFailed(string processDefinitionId, string error);

    [LoggerMessage(EventId = 13003, Level = LogLevel.Error, Message = "Error starting process definition {ProcessDefinitionId}")]
    private partial void LogProcessStartError(string processDefinitionId, Exception ex);

    [LoggerMessage(EventId = 13004, Level = LogLevel.Error, Message = "Error toggling process {ProcessDefinitionKey}")]
    private partial void LogToggleProcessError(string processDefinitionKey, Exception ex);

    [LoggerMessage(EventId = 13005, Level = LogLevel.Error, Message = "Error promoting process {ProcessDefinitionKey} to environment {Target}")]
    private partial void LogPromoteError(string processDefinitionKey, string target, Exception ex);
}
