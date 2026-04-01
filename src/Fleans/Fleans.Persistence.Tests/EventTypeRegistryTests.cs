using System.Dynamic;
using System.Reflection;
using Fleans.Domain.Events;
using Fleans.Persistence.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EventTypeRegistryTests
{
    private static readonly JsonSerializerSettings JsonSettings = EfCoreEventStore.JsonSettings;

    [TestMethod]
    public void GetEventType_ReturnsTypeName()
    {
        var evt = new WorkflowStarted(Guid.NewGuid(), "proc:1", Guid.NewGuid());
        Assert.AreEqual("WorkflowStarted", EventTypeRegistry.GetEventType(evt));
    }

    [TestMethod]
    public void Deserialize_ThrowsForUnknownDiscriminator()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(
            () => EventTypeRegistry.Deserialize("NonExistentEvent", "{}", JsonSettings));
    }

    [TestMethod]
    public void RoundTrip_AllEvents_PreservesData()
    {
        var events = CreateAllDomainEvents();
        foreach (var (evt, _) in events)
        {
            var eventType = EventTypeRegistry.GetEventType(evt);
            var json = JsonConvert.SerializeObject(evt, JsonSettings);
            var deserialized = EventTypeRegistry.Deserialize(eventType, json, JsonSettings);

            Assert.AreEqual(evt.GetType(), deserialized.GetType(),
                $"Type mismatch for {evt.GetType().Name}");
        }
    }

    [TestMethod]
    public void RoundTrip_WorkflowStarted_PreservesFields()
    {
        var original = new WorkflowStarted(Guid.NewGuid(), "process:1:abc", Guid.NewGuid());
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (WorkflowStarted)EventTypeRegistry.Deserialize(
            nameof(WorkflowStarted), json, JsonSettings);

        Assert.AreEqual(original.InstanceId, deserialized.InstanceId);
        Assert.AreEqual(original.ProcessDefinitionId, deserialized.ProcessDefinitionId);
        Assert.AreEqual(original.RootVariablesId, deserialized.RootVariablesId);
    }

    [TestMethod]
    public void RoundTrip_UserTaskClaimed_PreservesClaimedAt()
    {
        var original = new UserTaskClaimed(Guid.NewGuid(), "user-42", DateTimeOffset.UtcNow);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (UserTaskClaimed)EventTypeRegistry.Deserialize(
            nameof(UserTaskClaimed), json, JsonSettings);

        Assert.AreEqual(original.ActivityInstanceId, deserialized.ActivityInstanceId);
        Assert.AreEqual(original.UserId, deserialized.UserId);
        Assert.AreEqual(original.ClaimedAt, deserialized.ClaimedAt);
    }

    [TestMethod]
    public void RoundTrip_ActivityCompleted_PreservesExpandoObject()
    {
        var variables = new ExpandoObject();
        var dict = (IDictionary<string, object?>)variables;
        dict["count"] = 42L;
        dict["name"] = "test";

        var original = new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), variables);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (ActivityCompleted)EventTypeRegistry.Deserialize(
            nameof(ActivityCompleted), json, JsonSettings);

        Assert.AreEqual(original.ActivityInstanceId, deserialized.ActivityInstanceId);
        Assert.AreEqual(original.VariablesId, deserialized.VariablesId);

        var deserializedDict = (IDictionary<string, object?>)deserialized.Variables;
        Assert.AreEqual(42L, deserializedDict["count"]);
        Assert.AreEqual("test", deserializedDict["name"]);
    }

    [TestMethod]
    public void RoundTrip_VariablesMerged_PreservesExpandoObject()
    {
        var variables = new ExpandoObject();
        var dict = (IDictionary<string, object?>)variables;
        dict["status"] = "active";

        var original = new VariablesMerged(Guid.NewGuid(), variables);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (VariablesMerged)EventTypeRegistry.Deserialize(
            nameof(VariablesMerged), json, JsonSettings);

        var deserializedDict = (IDictionary<string, object?>)deserialized.Variables;
        Assert.AreEqual("active", deserializedDict["status"]);
    }

    [TestMethod]
    public void RoundTrip_ActivitySpawned_PreservesAllFields()
    {
        var original = new ActivitySpawned(
            Guid.NewGuid(), "task1", "ScriptTask",
            Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid());
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (ActivitySpawned)EventTypeRegistry.Deserialize(
            nameof(ActivitySpawned), json, JsonSettings);

        Assert.AreEqual(original.ActivityInstanceId, deserialized.ActivityInstanceId);
        Assert.AreEqual(original.ActivityId, deserialized.ActivityId);
        Assert.AreEqual(original.ActivityType, deserialized.ActivityType);
        Assert.AreEqual(original.VariablesId, deserialized.VariablesId);
        Assert.AreEqual(original.ScopeId, deserialized.ScopeId);
        Assert.AreEqual(original.MultiInstanceIndex, deserialized.MultiInstanceIndex);
        Assert.AreEqual(original.TokenId, deserialized.TokenId);
    }

    [TestMethod]
    public void RoundTrip_ConditionSequencesAdded_PreservesStringArray()
    {
        var original = new ConditionSequencesAdded(
            Guid.NewGuid(), new[] { "flow1", "flow2", "flow3" });
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (ConditionSequencesAdded)EventTypeRegistry.Deserialize(
            nameof(ConditionSequencesAdded), json, JsonSettings);

        CollectionAssert.AreEqual(original.SequenceFlowIds, deserialized.SequenceFlowIds);
    }

    [TestMethod]
    public void RoundTrip_VariableScopesRemoved_PreservesGuidList()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var original = new VariableScopesRemoved(ids);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (VariableScopesRemoved)EventTypeRegistry.Deserialize(
            nameof(VariableScopesRemoved), json, JsonSettings);

        Assert.AreEqual(original.ScopeIds.Count, deserialized.ScopeIds.Count);
        for (int i = 0; i < original.ScopeIds.Count; i++)
            Assert.AreEqual(original.ScopeIds[i], deserialized.ScopeIds[i]);
    }

    [TestMethod]
    public void RoundTrip_TimerCycleUpdated_PreservesTimerDefinition()
    {
        var timer = new Fleans.Domain.Activities.TimerDefinition(
            Fleans.Domain.Activities.TimerType.Cycle, "R2/PT5S");
        var original = new TimerCycleUpdated(Guid.NewGuid(), "timer1", timer);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (TimerCycleUpdated)EventTypeRegistry.Deserialize(
            nameof(TimerCycleUpdated), json, JsonSettings);

        Assert.AreEqual(original.HostActivityInstanceId, deserialized.HostActivityInstanceId);
        Assert.AreEqual(original.TimerActivityId, deserialized.TimerActivityId);
        Assert.IsNotNull(deserialized.RemainingCycle);
        Assert.AreEqual(timer.Type, deserialized.RemainingCycle.Type);
        Assert.AreEqual(timer.Expression, deserialized.RemainingCycle.Expression);
    }

    [TestMethod]
    public void RoundTrip_TimerCycleUpdated_PreservesNullTimer()
    {
        var original = new TimerCycleUpdated(Guid.NewGuid(), "timer1", null);
        var json = JsonConvert.SerializeObject(original, JsonSettings);
        var deserialized = (TimerCycleUpdated)EventTypeRegistry.Deserialize(
            nameof(TimerCycleUpdated), json, JsonSettings);

        Assert.IsNull(deserialized.RemainingCycle);
    }

    [TestMethod]
    public void WeakSchema_ExtraPropertiesInJson_DoNotCauseErrors()
    {
        var json = """{"InstanceId":"00000000-0000-0000-0000-000000000001","ProcessDefinitionId":"test","NewField":"extra"}""";
        var deserialized = (WorkflowStarted)EventTypeRegistry.Deserialize(
            nameof(WorkflowStarted), json, JsonSettings);

        Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), deserialized.InstanceId);
        Assert.AreEqual("test", deserialized.ProcessDefinitionId);
    }

    [TestMethod]
    public void WeakSchema_MissingNullableProperties_DefaultCorrectly()
    {
        var json = """{"ActivityInstanceId":"00000000-0000-0000-0000-000000000001","ActivityId":"a","ActivityType":"ScriptTask","VariablesId":"00000000-0000-0000-0000-000000000002"}""";
        var deserialized = (ActivitySpawned)EventTypeRegistry.Deserialize(
            nameof(ActivitySpawned), json, JsonSettings);

        Assert.AreEqual("a", deserialized.ActivityId);
        Assert.IsNull(deserialized.ScopeId);
        Assert.IsNull(deserialized.MultiInstanceIndex);
        Assert.IsNull(deserialized.TokenId);
    }

    private static List<(IDomainEvent Event, string ExpectedName)> CreateAllDomainEvents()
    {
        var id = Guid.NewGuid();
        var rootVarsId = Guid.NewGuid();
        var variables = new ExpandoObject();

        return
        [
            (new WorkflowStarted(id, "proc:1", rootVarsId), nameof(WorkflowStarted)),
            (new ExecutionStarted(), nameof(ExecutionStarted)),
            (new WorkflowCompleted(), nameof(WorkflowCompleted)),
            (new ActivitySpawned(id, "a", "ScriptTask", id, null, null, null), nameof(ActivitySpawned)),
            (new ActivityExecutionStarted(id), nameof(ActivityExecutionStarted)),
            (new ActivityCompleted(id, id, variables), nameof(ActivityCompleted)),
            (new ActivityFailed(id, 500, "error"), nameof(ActivityFailed)),
            (new ActivityExecutionReset(id), nameof(ActivityExecutionReset)),
            (new ActivityCancelled(id, "cancelled"), nameof(ActivityCancelled)),
            (new MultiInstanceTotalSet(id, 3), nameof(MultiInstanceTotalSet)),
            (new VariablesMerged(id, variables), nameof(VariablesMerged)),
            (new ChildVariableScopeCreated(id, id), nameof(ChildVariableScopeCreated)),
            (new VariableScopeCloned(id, id), nameof(VariableScopeCloned)),
            (new VariableScopesRemoved(new List<Guid> { id }), nameof(VariableScopesRemoved)),
            (new ConditionSequencesAdded(id, ["f1"]), nameof(ConditionSequencesAdded)),
            (new ConditionSequenceEvaluated(id, "f1", true), nameof(ConditionSequenceEvaluated)),
            (new GatewayForkCreated(id, null), nameof(GatewayForkCreated)),
            (new GatewayForkTokenAdded(id, id), nameof(GatewayForkTokenAdded)),
            (new GatewayForkRemoved(id), nameof(GatewayForkRemoved)),
            (new ParentInfoSet(id, "parentAct"), nameof(ParentInfoSet)),
            (new ChildWorkflowLinked(id, id), nameof(ChildWorkflowLinked)),
            (new TimerCycleUpdated(id, "timer1", null), nameof(TimerCycleUpdated)),
        ];
    }
}
