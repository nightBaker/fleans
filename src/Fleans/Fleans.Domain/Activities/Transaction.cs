namespace Fleans.Domain.Activities;

/// <summary>
/// BPMN Transaction Sub-Process. Semantic marker extending SubProcess;
/// the executor identifies it via <c>is Transaction</c> checks.
/// Three terminal outcomes: Completed, Cancelled (triggered by Cancel End Event via #230),
/// and Hazard (unhandled error escaping the scope).
/// Nesting (<c>&lt;transaction&gt;</c> inside another <c>&lt;transaction&gt;</c>) is fully supported:
/// all three terminal outcomes (Completed, Cancelled, Hazard) work correctly in nested contexts (#307 Phases A–C).
/// </summary>
[GenerateSerializer]
public sealed record Transaction(string ActivityId) : SubProcess(ActivityId);
