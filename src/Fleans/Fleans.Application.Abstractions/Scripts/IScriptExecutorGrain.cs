using System.Dynamic;
using Fleans.Domain.Events;
using Orleans.Concurrency;

namespace Fleans.Application.Scripts;

public interface IScriptExecutorGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Runs a script task's script on <paramref name="variables"/> and reports the outcome to the
    /// workflow instance with <c>IWorkflowInstanceCallback.CompleteActivity</c> or
    /// <c>FailActivity</c>. One-way: the instance dispatches and ends its turn instead of waiting
    /// for the script, and re-dispatches when no result arrives in time — so a script may run more
    /// than once, and only its first result is applied.
    /// </summary>
    [OneWay]
    Task Run(ExecuteScriptEvent request, ExpandoObject variables);
}
