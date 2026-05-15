using System.Reflection;
using Fleans.Worker.CustomTasks;
using Fleans.Worker.Placement;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Placement;

namespace Fleans.Application.Tests.Placement;

[TestClass]
public class PluginHostPlacementTests
{
    [TestMethod]
    public void CustomTaskHandlerBase_ShouldNotCarry_WorkerPlacementAttribute()
    {
        // Plugin handlers must use Orleans default placement so GetCompatibleSilos
        // (assembly-loading) is the only thing that decides where they activate.
        var attr = typeof(CustomTaskHandlerBase).GetCustomAttribute<WorkerPlacementAttribute>(inherit: false);

        Assert.IsNull(attr,
            "[WorkerPlacement] on CustomTaskHandlerBase causes plugin grains to land on " +
            "worker-/combined- silos that may not have the plugin's DLL loaded. Remove it; " +
            "rely on Orleans' GetCompatibleSilos for per-plugin isolation.");
    }

    [TestMethod]
    public void RestCallerHandler_ShouldNotCarry_WorkerPlacementAttribute()
    {
        var attr = typeof(Fleans.Plugins.RestCaller.RestCallerHandler)
            .GetCustomAttribute<WorkerPlacementAttribute>(inherit: false);

        Assert.IsNull(attr,
            "Concrete plugin handlers must not carry [WorkerPlacement]; default placement " +
            "with GetCompatibleSilos is the isolation primitive.");
    }
}
