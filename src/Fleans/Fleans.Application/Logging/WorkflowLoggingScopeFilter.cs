using Fleans.Application.Grains;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Logging;

public class WorkflowLoggingScopeFilter : IIncomingGrainCallFilter
{
    private readonly ILogger<WorkflowLoggingScopeFilter> _logger;

    public WorkflowLoggingScopeFilter(ILogger<WorkflowLoggingScopeFilter> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // WorkflowInstance manages its own scope via BeginWorkflowScope() — skip to avoid duplicates
        if (context.TargetContext.GrainInstance is IWorkflowInstanceGrain)
        {
            await context.Invoke();
            return;
        }

        var wid = RequestContext.Get(WorkflowContextKeys.WorkflowId) as string;
        var pdid = RequestContext.Get(WorkflowContextKeys.ProcessDefinitionId) as string;
        var wiid = RequestContext.Get(WorkflowContextKeys.WorkflowInstanceId) as string;
        var aid = RequestContext.Get(WorkflowContextKeys.ActivityId) as string;
        var aiid = RequestContext.Get(WorkflowContextKeys.ActivityInstanceId) as string;
        var vid = RequestContext.Get(WorkflowContextKeys.VariablesId) as string;

        if (wid is not null || wiid is not null)
        {
            using (_logger.BeginScope(
                "[{WorkflowId}, {ProcessDefinitionId}, {WorkflowInstanceId}, {ActivityId}, {ActivityInstanceId}, {VariablesId}]",
                wid ?? "-", pdid ?? "-", wiid ?? "-", aid ?? "-", aiid ?? "-", vid ?? "-"))
                await context.Invoke();
        }
        else
        {
            await context.Invoke();
        }
    }
}
