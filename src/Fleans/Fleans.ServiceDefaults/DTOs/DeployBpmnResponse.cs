namespace Fleans.ServiceDefaults.DTOs;

/// <param name="Warnings">What the deploy warned about, such as custom-task inputs it couldn't check.</param>
public record DeployBpmnResponse(string ProcessDefinitionKey, int Version, List<string>? Warnings = null);
