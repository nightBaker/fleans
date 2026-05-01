using Fleans.Domain.Activities;
using Fleans.Domain.Errors;

namespace Fleans.Domain.Tests;

[TestClass]
public class CustomTaskActivityTests
{
    [TestMethod]
    public void Ctor_StoresAllProperties()
    {
        var inputs = new[] { new InputMapping("=orderId", "id") };
        var outputs = new[] { new OutputMapping("=__response.body", "result") };

        var task = new CustomTaskActivity("ct1", "rest-call", inputs, outputs);

        Assert.AreEqual("ct1", task.ActivityId);
        Assert.AreEqual("rest-call", task.TaskType);
        Assert.HasCount(1, task.InputMappings);
        Assert.AreEqual("=orderId", task.InputMappings[0].Source);
        Assert.AreEqual("id", task.InputMappings[0].Target);
        Assert.HasCount(1, task.OutputMappings);
        Assert.AreEqual("=__response.body", task.OutputMappings[0].Source);
        Assert.AreEqual("result", task.OutputMappings[0].Target);
    }

    [TestMethod]
    public void Ctor_AllowsNullMappings_DefaultsToEmptyLists()
    {
        var task = new CustomTaskActivity("ct1", "rest-call", null, null);

        Assert.IsNotNull(task.InputMappings);
        Assert.IsNotNull(task.OutputMappings);
        Assert.HasCount(0, task.InputMappings);
        Assert.HasCount(0, task.OutputMappings);
    }

    [TestMethod]
    public void Ctor_ThrowsOnNullTaskType()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new CustomTaskActivity("ct1", null!, null, null));
    }

    [TestMethod]
    public void CustomTaskFailedActivityException_MapsCodeAndMessage()
    {
        var ex = new CustomTaskFailedActivityException("503", "executor unreachable");

        var state = ex.GetActivityErrorState();

        Assert.AreEqual("503", state.Code);
        Assert.AreEqual("executor unreachable", state.Message);
    }
}
