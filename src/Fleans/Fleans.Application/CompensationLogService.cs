using Fleans.Application.Grains;
using Fleans.Application.QueryModels;

namespace Fleans.Application;

/// <inheritdoc cref="ICompensationLogService"/>
public sealed class CompensationLogService : ICompensationLogService
{
    private readonly IGrainFactory _grainFactory;

    public CompensationLogService(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<IReadOnlyList<CompensationLogEntrySnapshot>> GetCompensationLog(Guid workflowInstanceId)
    {
        var grain = _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        return await grain.GetCompensationLog();
    }
}
