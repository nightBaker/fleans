using System.Dynamic;
using System.Reflection;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Persistence.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EventTypeRegistryTests
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
        SerializationBinder = new DomainAssemblySerializationBinder()
    };

    [TestMethod]
    public void RoundTrip_WorkflowStarted()
    {
        var evt = new WorkflowStarted(Guid.NewGuid(), "process-1");
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ExecutionStarted()
    {
        var evt = new ExecutionStarted();
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_WorkflowCompleted()
    {
        var evt = new WorkflowCompleted();
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivitySpawned()
    {
        var evt = new ActivitySpawned(
            Guid.NewGuid(), "task-1", "ScriptTask",
            Guid.NewGuid(), Guid.NewGuid(), 3, Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivitySpawned_NullOptionalFields()
    {
        var evt = new ActivitySpawned(
            Guid.NewGuid(), "task-1", "ScriptTask",
            Guid.NewGuid(), null, null, null);
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivityExecutionStarted()
    {
        var evt = new ActivityExecutionStarted(Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivityCompleted_WithExpandoObject()
    {
        dynamic variables = new ExpandoObject();
        variables.x = 42;
        variables.name = "test";
        variables.nested = new ExpandoObject();
        variables.nested.inner = "value";

        var evt = new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), (ExpandoObject)variables);
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (ActivityCompleted)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.ActivityInstanceId, deserialized.ActivityInstanceId);
        Assert.AreEqual(evt.VariablesId, deserialized.VariablesId);

        var dict = (IDictionary<string, object?>)deserialized.Variables;
        Assert.AreEqual(42L, dict["x"]); // JSON numbers deserialize as long
        Assert.AreEqual("test", dict["name"]);

        var nested = (IDictionary<string, object?>)dict["nested"]!;
        Assert.AreEqual("value", nested["inner"]);
    }

    [TestMethod]
    public void RoundTrip_ActivityFailed()
    {
        var evt = new ActivityFailed(Guid.NewGuid(), 500, "Something went wrong");
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivityExecutionReset()
    {
        var evt = new ActivityExecutionReset(Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ActivityCancelled()
    {
        var evt = new ActivityCancelled(Guid.NewGuid(), "Boundary timer fired");
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_MultiInstanceTotalSet()
    {
        var evt = new MultiInstanceTotalSet(Guid.NewGuid(), 5);
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_VariablesMerged_WithExpandoObject()
    {
        dynamic variables = new ExpandoObject();
        variables.result = "ok";
        variables.count = 10;

        var evt = new VariablesMerged(Guid.NewGuid(), (ExpandoObject)variables);
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (VariablesMerged)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.VariablesId, deserialized.VariablesId);
        var dict = (IDictionary<string, object?>)deserialized.Variables;
        Assert.AreEqual("ok", dict["result"]);
        Assert.AreEqual(10L, dict["count"]);
    }

    [TestMethod]
    public void RoundTrip_ChildVariableScopeCreated()
    {
        var evt = new ChildVariableScopeCreated(Guid.NewGuid(), Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_VariableScopeCloned()
    {
        var evt = new VariableScopeCloned(Guid.NewGuid(), Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_VariableScopesRemoved()
    {
        var evt = new VariableScopesRemoved(new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (VariableScopesRemoved)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.ScopeIds.Count, deserialized.ScopeIds.Count);
        for (int i = 0; i < evt.ScopeIds.Count; i++)
            Assert.AreEqual(evt.ScopeIds[i], deserialized.ScopeIds[i]);
    }

    [TestMethod]
    public void RoundTrip_ConditionSequencesAdded()
    {
        var gatewayId = Guid.NewGuid();
        var evt = new ConditionSequencesAdded(gatewayId, new[] { "seq-1", "seq-2" });
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (ConditionSequencesAdded)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.GatewayInstanceId, deserialized.GatewayInstanceId);
        CollectionAssert.AreEqual(evt.SequenceFlowIds, deserialized.SequenceFlowIds);
    }

    [TestMethod]
    public void RoundTrip_ConditionSequenceEvaluated()
    {
        var evt = new ConditionSequenceEvaluated(Guid.NewGuid(), "seq-1", true);
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_GatewayForkCreated()
    {
        var evt = new GatewayForkCreated(Guid.NewGuid(), Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_GatewayForkTokenAdded()
    {
        var evt = new GatewayForkTokenAdded(Guid.NewGuid(), Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_GatewayForkRemoved()
    {
        var evt = new GatewayForkRemoved(Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ParentInfoSet()
    {
        var evt = new ParentInfoSet(Guid.NewGuid(), "parent-activity");
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_ChildWorkflowLinked()
    {
        var evt = new ChildWorkflowLinked(Guid.NewGuid(), Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_UserTaskRegistered()
    {
        var evt = new UserTaskRegistered(
            Guid.NewGuid(), "user-1",
            new List<string> { "group-a", "group-b" }, new List<string> { "user-x" }, new List<string> { "var1", "var2" });
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (UserTaskRegistered)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.ActivityInstanceId, deserialized.ActivityInstanceId);
        Assert.AreEqual(evt.Assignee, deserialized.Assignee);
        Assert.AreEqual(evt.CandidateGroups.Count, deserialized.CandidateGroups.Count);
        Assert.AreEqual(evt.CandidateUsers.Count, deserialized.CandidateUsers.Count);
        Assert.AreEqual(evt.ExpectedOutputVariables!.Count, deserialized.ExpectedOutputVariables!.Count);
    }

    [TestMethod]
    public void RoundTrip_UserTaskRegistered_NullOptionals()
    {
        var evt = new UserTaskRegistered(
            Guid.NewGuid(), null,
            new List<string>(), new List<string>(), null);
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (UserTaskRegistered)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.IsNull(deserialized.Assignee);
        Assert.IsNull(deserialized.ExpectedOutputVariables);
    }

    [TestMethod]
    public void RoundTrip_UserTaskClaimed()
    {
        var evt = new UserTaskClaimed(Guid.NewGuid(), "user-1");
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_UserTaskUnclaimed()
    {
        var evt = new UserTaskUnclaimed(Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_UserTaskUnregistered()
    {
        var evt = new UserTaskUnregistered(Guid.NewGuid());
        AssertRoundTrip(evt);
    }

    [TestMethod]
    public void RoundTrip_TimerCycleUpdated()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10S");
        var evt = new TimerCycleUpdated(Guid.NewGuid(), "timer-1", timer);
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (TimerCycleUpdated)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.AreEqual(evt.HostActivityInstanceId, deserialized.HostActivityInstanceId);
        Assert.AreEqual(evt.TimerActivityId, deserialized.TimerActivityId);
        Assert.IsNotNull(deserialized.RemainingCycle);
        Assert.AreEqual(evt.RemainingCycle!.Type, deserialized.RemainingCycle.Type);
        Assert.AreEqual(evt.RemainingCycle.Expression, deserialized.RemainingCycle.Expression);
    }

    [TestMethod]
    public void RoundTrip_TimerCycleUpdated_NullCycle()
    {
        var evt = new TimerCycleUpdated(Guid.NewGuid(), "timer-1", null);
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        var json = EventTypeRegistry.Serialize(evt, Settings);
        var deserialized = (TimerCycleUpdated)EventTypeRegistry.Deserialize(typeName, json, Settings);

        Assert.IsNull(deserialized.RemainingCycle);
    }

    [TestMethod]
    public void GetEventType_UnknownTypeName_ThrowsKeyNotFoundException()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(
            () => EventTypeRegistry.GetEventType("NonExistentEvent"));
    }

    [TestMethod]
    public void Deserialize_UnknownTypeName_ThrowsKeyNotFoundException()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(
            () => EventTypeRegistry.Deserialize("NonExistentEvent", "{}", Settings));
    }

    [TestMethod]
    public void WeakSchema_ExtraFieldInJson_IsIgnored()
    {
        var json = """{"ActivityInstanceId":"00000000-0000-0000-0000-000000000001","ExtraField":"should be ignored"}""";
        var deserialized = (ActivityExecutionStarted)EventTypeRegistry.Deserialize(
            "ActivityExecutionStarted", json, Settings);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), deserialized.ActivityInstanceId);
    }

    [TestMethod]
    public void WeakSchema_MissingNullableField_DefaultsCorrectly()
    {
        // WorkflowStarted has nullable ProcessDefinitionId — omit it from JSON
        var json = """{"InstanceId":"00000000-0000-0000-0000-000000000001"}""";
        var deserialized = (WorkflowStarted)EventTypeRegistry.Deserialize(
            "WorkflowStarted", json, Settings);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), deserialized.InstanceId);
        Assert.IsNull(deserialized.ProcessDefinitionId);
    }

    [TestMethod]
    public void AllDomainEvents_AreRegistered()
    {
        // Infrastructure events used for grain pub/sub are excluded from the event store.
        // They carry [GenerateSerializer] and are not persisted as workflow state mutations.
        var infrastructureEvents = new HashSet<Type>
        {
            typeof(WorkflowActivityExecutedEvent),
            typeof(EvaluateConditionEvent),
            typeof(ExecuteScriptEvent),
        };

        var domainAssembly = typeof(IDomainEvent).Assembly;
        var domainEventTypes = domainAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDomainEvent).IsAssignableFrom(t))
            .Except(infrastructureEvents)
            .ToHashSet();

        var registeredTypes = EventTypeRegistry.AllEventTypes.ToHashSet();

        var unregistered = domainEventTypes.Except(registeredTypes).ToList();
        Assert.AreEqual(0, unregistered.Count,
            $"Unregistered IDomainEvent types: {string.Join(", ", unregistered.Select(t => t.Name))}");
    }

    [TestMethod]
    public void GetEventTypeName_FromInstance_MatchesGeneric()
    {
        var evt = new WorkflowStarted(Guid.NewGuid(), "p1");
        var fromInstance = EventTypeRegistry.GetEventTypeName(evt);
        var fromGeneric = EventTypeRegistry.GetEventTypeName<WorkflowStarted>();
        Assert.AreEqual(fromInstance, fromGeneric);
        Assert.AreEqual("WorkflowStarted", fromInstance);
    }

    private static void AssertRoundTrip<T>(T evt) where T : IDomainEvent
    {
        var typeName = EventTypeRegistry.GetEventTypeName(evt);
        Assert.AreEqual(typeof(T).Name, typeName);

        var json = EventTypeRegistry.Serialize(evt, Settings);
        Assert.IsFalse(string.IsNullOrEmpty(json));

        var deserialized = EventTypeRegistry.Deserialize(typeName, json, Settings);
        Assert.IsInstanceOfType<T>(deserialized);
        Assert.AreEqual(evt, deserialized);
    }
}
