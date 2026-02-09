using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class ExclusiveGatewayTests
{
    private TestCluster _cluster = null!;

    [TestInitialize]
    public void Setup()
    {
        _cluster = CreateCluster();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cluster?.StopAllSilos();
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeTrueBranch_WhenFirstConditionIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition true, gateway should auto-complete
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed via end1 (true branch)
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "end1");
        CollectionAssert.DoesNotContain(completedIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldShortCircuit_OnFirstTrueCondition()
    {
        // Arrange — gateway with two conditional flows, first returns true
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition is true -> should complete immediately
        // Second condition is never evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed without needing seq3
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var activeActivities = await state.GetActiveActivities();
        Assert.AreEqual(0, activeActivities.Count);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeSecondBranch_WhenFirstIsFalseSecondIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second true
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", true);

        // Assert — workflow completed via end2
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "end2");
        CollectionAssert.DoesNotContain(completedIds, "end1");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeDefaultFlow_WhenAllConditionsAreFalse()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultFlow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — all conditions false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", false);

        // Assert — workflow completed via endDefault (default flow)
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "endDefault");
        CollectionAssert.DoesNotContain(completedIds, "end1");
        CollectionAssert.DoesNotContain(completedIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldThrow_WhenAllConditionsFalse_AndNoDefaultFlow()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(); // no default flow
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — second condition false should throw
        // Orleans may wrap the exception, so we catch the broadest type
        var threw = false;
        try
        {
            await workflowInstance.CompleteConditionSequence("if", "seq3", false);
        }
        catch (Exception ex)
        {
            threw = true;
            // The InvalidOperationException may be thrown directly or wrapped by Orleans
            var innerMost = GetInnermostException(ex);
            Assert.IsInstanceOfType<InvalidOperationException>(innerMost,
                $"Expected InvalidOperationException but got {innerMost.GetType().Name}: {innerMost.Message}");
        }

        Assert.IsTrue(threw, "Expected an exception when all conditions are false with no default flow");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldPassThrough_WhenOnlyDefaultFlowExists()
    {
        // Arrange — gateway with no conditional flows, only a default flow
        var workflow = CreateWorkflowWithOnlyDefaultFlow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act — start workflow, gateway should auto-complete immediately
        await workflowInstance.StartWorkflow();

        // Assert — workflow completed via endDefault
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "start");
        CollectionAssert.Contains(completedIds, "if");
        CollectionAssert.Contains(completedIds, "endDefault");

        var activeActivities = await state.GetActiveActivities();
        Assert.AreEqual(0, activeActivities.Count);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldNotAutoComplete_WhenConditionsStillPending()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second not yet evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — workflow not completed, gateway still active
        var state = await workflowInstance.GetState();
        Assert.IsFalse(await state.IsCompleted());

        var activeActivities = await state.GetActiveActivities();
        Assert.IsTrue(activeActivities.Count > 0);
    }

    private static Exception GetInnermostException(Exception ex)
    {
        while (ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }

    private static async Task<List<string>> GetCompletedActivityIds(IWorkflowInstanceState state)
    {
        var completed = await state.GetCompletedActivities();
        var ids = new List<string>();
        foreach (var activity in completed)
        {
            var current = await activity.GetCurrentActivity();
            ids.Add(current.ActivityId);
        }
        return ids;
    }

    private static IWorkflowDefinition CreateWorkflowWithTwoBranches()
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow1",
            Activities = [start, ifActivity, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition")
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithOnlyDefaultFlow()
    {
        var start = new StartEvent("start");
        var endDefault = new EndEvent("endDefault");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow-only-default",
            Activities = [start, ifActivity, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithDefaultFlow()
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var endDefault = new EndEvent("endDefault");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow-default",
            Activities = [start, ifActivity, end1, end2, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "condition1"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "condition2"),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }

    private static TestCluster CreateCluster()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        var cluster = builder.Build();
        cluster.Deploy();
        return cluster;
    }

    class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
           hostBuilder.ConfigureServices(services => services.AddSerializer(serializerBuilder =>
           {
               serializerBuilder.AddNewtonsoftJsonSerializer(
                   isSupported: type => type == typeof(ExpandoObject),
                   new Newtonsoft.Json.JsonSerializerSettings
                   {
                       TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                   });
           }));
    }
}

/// <summary>
/// Mock implementation of IEventPublisher used by all Domain.Tests requiring Orleans grain execution.
/// Orleans auto-discovers this grain implementation from the test assembly.
/// </summary>
public class EventPublisherMock : Grain, IEventPublisher
{
    public Task Publish(IDomainEvent domainEvent)
    {
        return Task.CompletedTask;
    }
}
