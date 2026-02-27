using Fleans.Domain.Activities;

namespace Fleans.Domain;

public interface IExecutionCommand { }

[GenerateSerializer]
public record CompleteCommand() : IExecutionCommand;

[GenerateSerializer]
public record SpawnActivityCommand(
    [property: Id(0)] Activity Activity,
    [property: Id(1)] Guid? ScopeId,
    [property: Id(2)] Guid? HostActivityInstanceId) : IExecutionCommand;

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
