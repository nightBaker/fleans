using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class EndEventTests
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
    public async Task ExecuteAsync_ShouldCompleteActivity_AndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete the task so EndEvent fires
        var variables = new ExpandoObject();
        await workflowInstance.CompleteActivity("task", variables);

        // Assert
        var snapshot = await workflowInstance.GetStateSnapshot();
        var endActivity = snapshot.CompletedActivities.FirstOrDefault(a => a.ActivityType == "EndEvent");

        Assert.IsNotNull(endActivity);
        Assert.IsTrue(endActivity.IsCompleted);
        Assert.IsTrue(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldAlwaysReturnEmptyList()
    {
        // Arrange
        var workflow = CreateSimpleWorkflow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — complete the task so EndEvent executes
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert — workflow is completed and no active activities remain,
        // proving EndEvent has no next activities
        var snapshot = await workflowInstance.GetStateSnapshot();
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.HasCount(0, snapshot.ActiveActivities);
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
