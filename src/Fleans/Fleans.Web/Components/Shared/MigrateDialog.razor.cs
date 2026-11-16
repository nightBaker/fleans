using Microsoft.AspNetCore.Components;

namespace Fleans.Web.Components.Shared;

public partial class MigrateDialog
{
    [Inject] private ILogger<MigrateDialog> Logger { get; set; } = null!;

    [LoggerMessage(EventId = 13094, Level = LogLevel.Warning,
        Message = "Error loading the migration preview of process {ProcessDefinitionKey} v{SourceVersion}")]
    private partial void LogPreviewError(string processDefinitionKey, int sourceVersion, Exception ex);

    [LoggerMessage(EventId = 13095, Level = LogLevel.Error,
        Message = "Error migrating instances of process {ProcessDefinitionKey} v{SourceVersion} to v{TargetVersion}")]
    private partial void LogMigrateError(string processDefinitionKey, int sourceVersion, int targetVersion, Exception ex);
}
