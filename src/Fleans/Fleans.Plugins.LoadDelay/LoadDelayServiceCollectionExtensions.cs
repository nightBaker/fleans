using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.LoadDelay;

/// <summary>
/// Wires <see cref="LoadDelayHandler"/> into a Worker silo's host. Registered only
/// in the load-test build of <c>Fleans.WorkerHost</c> (the MSBuild conditional
/// <c>Condition="'$(FleansLoadTestMode)' == 'true'"</c> on the project reference).
/// </summary>
public static class LoadDelayServiceCollectionExtensions
{
    public static IServiceCollection AddLoadDelayPlugin(this IServiceCollection services)
    {
        services.AddCustomTaskPlugin<LoadDelayHandler>(
            taskType: LoadDelayHandler.LoadDelayTaskType,
            displayName: "Load Delay (100ms)",
            parameterSchema: CustomTaskParameterSchema.Empty);

        return services;
    }
}
