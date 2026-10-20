using Fleans.Application.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace Fleans.Application.Tests;

[TestClass]
public class WorkflowLoggingContextTests
{
    [TestCleanup]
    public void Cleanup() => RequestContext.Clear();

    [TestMethod]
    public void SetInstance_ClearsActivityIdLeftByStreamHandler()
    {
        // Arrange — a stream handler scoped its call to one activity
        using (WorkflowLoggingContext.BeginWorkflowScope(
                   NullLogger.Instance, "wf", "pd", Guid.NewGuid(), activityId: "task1"))
        {
            Assert.AreEqual("task1", RequestContext.Get(WorkflowContextKeys.ActivityId));

            // Act — the instance grain points the context at itself before its outgoing calls
            WorkflowLoggingContext.SetInstance("wf", "pd", Guid.NewGuid(), businessId: null);

            // Assert
            Assert.IsNull(RequestContext.Get(WorkflowContextKeys.ActivityId));
        }
    }

    [TestMethod]
    public void BeginWorkflowScope_SetsActivityIdForTheHandlersOwnCall()
    {
        // Arrange
        RequestContext.Set(WorkflowContextKeys.ActivityId, "stale");

        // Act
        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            NullLogger.Instance, "wf", "pd", Guid.NewGuid(), activityId: "task2");

        // Assert
        Assert.AreEqual("task2", RequestContext.Get(WorkflowContextKeys.ActivityId));
    }
}
