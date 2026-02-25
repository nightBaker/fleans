using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Fleans.Application.Services;

public interface IBoundaryEventStateAccessor
{
    WorkflowInstanceState State { get; }
    IGrainFactory GrainFactory { get; }
    ILogger Logger { get; }
    IWorkflowExecutionContext WorkflowExecutionContext { get; }
    ValueTask<object?> GetVariable(Guid variablesId, string variableName);
    Task TransitionToNextActivity();
    Task ExecuteWorkflow();
    Task CancelScopeChildren(Guid scopeId);
}
