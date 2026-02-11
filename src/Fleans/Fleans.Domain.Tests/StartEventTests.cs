using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class StartEventTests
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
    public async Task ExecuteAsync_ShouldCompleteActivity_AndStartWorkflow()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert
        var snapshot = await workflowInstance.GetStateSnapshot();
        Assert.IsTrue(snapshot.IsStarted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "start" && a.IsCompleted));
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnFirstActivity_AfterStart()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = new List<Activity> { start, task, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            }
        };

        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — after start event completes, "task" should be the active activity
        var snapshot = await workflowInstance.GetStateSnapshot();
        Assert.HasCount(1, snapshot.ActiveActivities);
        Assert.AreEqual("task", snapshot.ActiveActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnEmptyList_WhenNoSequenceFlow()
    {
        // Arrange
        var start = new StartEvent("start");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = new List<Activity> { start },
            SequenceFlows = new List<SequenceFlow>()
        };

        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — start event completes but no next activity exists (no sequence flow)
        var snapshot = await workflowInstance.GetStateSnapshot();
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "start"));
        Assert.HasCount(0, snapshot.ActiveActivities);
        Assert.IsFalse(snapshot.IsCompleted);
    }

    private static TestCluster CreateCluster()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        var cluster = builder.Build();
        cluster.Deploy();
        return cluster;
    }

    private static IWorkflowDefinition CreateSimpleWorkflow()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, task, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            }
        };
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
