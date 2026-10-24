namespace Fleans.Application.QueryModels;

/// <summary>
/// One deployed version and a SHA-256 of its BPMN XML (#900 follow-up). Version numbers are
/// per cluster — v3 in dev and v3 in prod need not be the same diagram — so environments are
/// compared by <see cref="BpmnSha256"/>: a promoted version keeps its exact XML.
/// </summary>
public sealed record ProcessVersionFingerprint(
    string ProcessDefinitionKey,
    int Version,
    bool IsActive,
    DateTimeOffset DeployedAt,
    string BpmnSha256);
