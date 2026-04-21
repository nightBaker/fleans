using Fleans.Domain.Activities;

namespace Fleans.Domain;

public interface IExecutionCommand { }

[GenerateSerializer]
public record SpawnActivityCommand(
    [property: Id(0)] Activity Activity,
    [property: Id(1)] Guid? ScopeId,
    [property: Id(2)] Guid? HostActivityInstanceId) : IExecutionCommand
{
    [Id(3)] public int? MultiInstanceIndex { get; init; }
    [Id(5)] public Guid? ParentVariablesId { get; init; } // Id(4) intentionally skipped
    [Id(6)] public object? IterationItem { get; init; }
    [Id(7)] public string? IterationItemName { get; init; }

    public bool IsMultiInstanceIteration => MultiInstanceIndex is not null && ParentVariablesId is not null;
}

[GenerateSerializer]
public record OpenSubProcessCommand(
    [property: Id(0)] SubProcess SubProcess,
    [property: Id(1)] Guid ParentVariablesId) : IExecutionCommand;

[GenerateSerializer]
public record RegisterTimerCommand(
    [property: Id(0)] string TimerActivityId,
    [property: Id(1)] TimeSpan DueTime,
    [property: Id(2)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record RegisterMessageCommand(
    [property: Id(0)] Guid VariablesId,
    [property: Id(1)] string MessageDefinitionId,
    [property: Id(2)] string ActivityId,
    [property: Id(3)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record RegisterSignalCommand(
    [property: Id(0)] string SignalName,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] bool IsBoundary) : IExecutionCommand;

[GenerateSerializer]
public record StartChildWorkflowCommand(
    [property: Id(0)] CallActivity CallActivity) : IExecutionCommand;

[GenerateSerializer]
public record AddConditionsCommand(
    [property: Id(0)] string[] SequenceFlowIds,
    [property: Id(1)] List<ConditionEvaluation> Evaluations) : IExecutionCommand;

[GenerateSerializer]
public record ConditionEvaluation(
    [property: Id(0)] string SequenceFlowId,
    [property: Id(1)] string Condition);

[GenerateSerializer]
public record ThrowSignalCommand(
    [property: Id(0)] string SignalName) : IExecutionCommand;

[GenerateSerializer]
public record ThrowEscalationCommand(
    [property: Id(0)] string EscalationCode) : IExecutionCommand;

[GenerateSerializer]
public record RegisterUserTaskCommand(
    [property: Id(0)] string? Assignee,
    [property: Id(1)] IReadOnlyList<string> CandidateGroups,
    [property: Id(2)] IReadOnlyList<string> CandidateUsers,
    [property: Id(3)] IReadOnlyList<string>? ExpectedOutputVariables) : IExecutionCommand;

[GenerateSerializer]
public record CompleteWorkflowCommand() : IExecutionCommand;

[GenerateSerializer]
public record EvaluateActivationConditionCommand(
    [property: Id(0)] string Condition,
    [property: Id(1)] int NrOfToken) : IExecutionCommand;

/// <summary>
/// Emitted by a complex gateway join when a late-arriving token finds the gateway
/// already fired. The aggregate cancels this token's activity instance so it doesn't
/// remain active forever.
/// </summary>
[GenerateSerializer]
public record DiscardLateTokenCommand(
    [property: Id(0)] string Reason) : IExecutionCommand;

[GenerateSerializer]
public record RegisterConditionalWatcherCommand(
    [property: Id(0)] Guid VariablesId,
    [property: Id(1)] string ConditionExpression,
    [property: Id(2)] string ActivityId) : IExecutionCommand;

