using System.Dynamic;
using Fleans.Domain.Events;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDomainEventsTests
{
    [TestMethod]
    public void WorkflowStarted_ShouldHoldInstanceIdAndProcessDefinitionId()
    {
        var id = Guid.NewGuid();
        var evt = new WorkflowStarted(id, "process-1", Guid.NewGuid());
        Assert.AreEqual(id, evt.InstanceId);
        Assert.AreEqual("process-1", evt.ProcessDefinitionId);
    }

    [TestMethod]
    public void ActivitySpawned_ShouldHoldAllFields()
    {
        var evt = new ActivitySpawned(Guid.NewGuid(), "task1", "ScriptTask",
            Guid.NewGuid(), null, null, null);
        Assert.AreEqual("task1", evt.ActivityId);
        Assert.AreEqual("ScriptTask", evt.ActivityType);
    }

    [TestMethod]
    public void ActivityCompleted_ShouldHoldVariables()
    {
        var vars = new ExpandoObject();
        ((IDictionary<string, object?>)vars)["x"] = 10;
        var evt = new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), vars);
        Assert.AreEqual(10, ((IDictionary<string, object?>)evt.Variables)["x"]);
    }

    [TestMethod]
    public void AllDomainEvents_ShouldImplementIDomainEvent()
    {
        var events = new IDomainEvent[]
        {
            new WorkflowStarted(Guid.NewGuid(), "p1", Guid.NewGuid()),
            new WorkflowCompleted(),
            new ActivitySpawned(Guid.NewGuid(), "a1", "T", Guid.NewGuid(), null, null, null),
            new ActivityExecutionStarted(Guid.NewGuid()),
            new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), new ExpandoObject()),
            new ActivityFailed(Guid.NewGuid(), 500, "err"),
            new ActivityCancelled(Guid.NewGuid(), "reason"),
            new VariablesMerged(Guid.NewGuid(), new ExpandoObject()),
            new ChildVariableScopeCreated(Guid.NewGuid(), Guid.NewGuid()),
            new VariableScopeCloned(Guid.NewGuid(), Guid.NewGuid()),
            new VariableScopesRemoved([Guid.NewGuid()]),
            new ConditionSequencesAdded(Guid.NewGuid(), ["seq1"]),
            new ConditionSequenceEvaluated(Guid.NewGuid(), "seq1", true),
            new GatewayForkCreated(Guid.NewGuid(), null),
            new GatewayForkTokenAdded(Guid.NewGuid(), Guid.NewGuid()),
            new GatewayForkRemoved(Guid.NewGuid()),
            new ParentInfoSet(Guid.NewGuid(), "parentActivity"),
        };

        Assert.AreEqual(17, events.Length);
        foreach (var evt in events)
            Assert.IsInstanceOfType<IDomainEvent>(evt);
    }
}
