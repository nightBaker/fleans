using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using NSubstitute;

namespace Fleans.Domain.Tests;

[TestClass]
public class CustomTaskActivityTests
{
    [TestMethod]
    public void Ctor_StoresTaskType_MappingsDefaultToEmpty()
    {
        var task = new CustomTaskActivity("ct1", "rest-call");

        Assert.AreEqual("ct1", task.ActivityId);
        Assert.AreEqual("rest-call", task.TaskType);
        Assert.HasCount(0, task.InputMappings);
        Assert.HasCount(0, task.OutputMappings);
    }

    [TestMethod]
    public async Task ExecuteAsync_PublishesInputValuesFromTheLocalScope()
    {
        // Inputs are evaluated by the engine before the task executes; the event carries their values.
        var task = new CustomTaskActivity("ct1", "rest-call")
        {
            InputMappings = [new InputMapping("=order.Id", "id")]
        };
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetVariable(Arg.Any<Guid>(), "id").Returns(ValueTask.FromResult<object?>("O-1"));
        var (activityContext, published) = ActivityTestHelper.CreateActivityContext("ct1");

        await task.ExecuteAsync(workflowContext, activityContext, definition);

        var inputs = (IDictionary<string, object?>)published.OfType<ExecuteCustomTaskEvent>().Single().Inputs;
        Assert.HasCount(1, inputs);
        Assert.AreEqual("O-1", inputs["id"]);
    }

    [TestMethod]
    public void Ctor_ThrowsOnNullTaskType()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new CustomTaskActivity("ct1", null!));
    }

    [TestMethod]
    public async Task ExecuteAsync_PublishesInstanceBusinessId()
    {
        var task = new CustomTaskActivity("ct1", "rest-call");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        workflowContext.GetBusinessId().Returns(ValueTask.FromResult<string?>("ORDER-42"));
        var (activityContext, published) = ActivityTestHelper.CreateActivityContext("ct1");

        await task.ExecuteAsync(workflowContext, activityContext, definition);

        var evt = published.OfType<ExecuteCustomTaskEvent>().Single();
        Assert.AreEqual("ORDER-42", evt.BusinessId);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithoutBusinessId_PublishesNull()
    {
        var task = new CustomTaskActivity("ct1", "rest-call");
        var definition = ActivityTestHelper.CreateWorkflowDefinition([task], []);
        var workflowContext = ActivityTestHelper.CreateWorkflowContext(definition);
        var (activityContext, published) = ActivityTestHelper.CreateActivityContext("ct1");

        await task.ExecuteAsync(workflowContext, activityContext, definition);

        Assert.IsNull(published.OfType<ExecuteCustomTaskEvent>().Single().BusinessId);
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
