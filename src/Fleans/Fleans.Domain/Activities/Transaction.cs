namespace Fleans.Domain.Activities;

/// <summary>
/// BPMN Transaction Sub-Process. Semantic marker extending SubProcess;
/// the executor identifies it via <c>is Transaction</c> checks.
/// Three terminal outcomes: Completed, Cancelled (triggered by Cancel End Event via #230),
/// and Hazard (unhandled error escaping the scope).
/// Nested transactions are rejected at BPMN parse time (see BpmnConverter).
/// </summary>
[GenerateSerializer]
public sealed record Transaction(string ActivityId) : SubProcess(ActivityId);
