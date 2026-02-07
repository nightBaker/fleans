using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class ScriptTaskTests
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
    public void ScriptTask_ShouldHaveCorrectProperties()
    {
        // Arrange & Act
        var task = new ScriptTask("script1", "_context.x = 10", "csharp");

        // Assert
        Assert.AreEqual("script1", task.ActivityId);
        Assert.AreEqual("_context.x = 10", task.Script);
        Assert.AreEqual("csharp", task.ScriptFormat);
    }

    [TestMethod]
    public void ScriptTask_ShouldDefaultScriptFormatToCsharp()
    {
        // Arrange & Act
        var task = new ScriptTask("script1", "_context.x = 10");

        // Assert
        Assert.AreEqual("csharp", task.ScriptFormat);
    }

    [TestMethod]
    public async Task GetNextActivities_ShouldReturnNextActivity()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.x = 10");
        var end = new EndEvent("end");
        var start = new StartEvent("start");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = new List<Activity> { start, script, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, script),
                new SequenceFlow("seq2", script, end)
            }
        };

        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        var activityInstance = _cluster.GrainFactory.GetGrain<IActivityInstance>(Guid.NewGuid());
        await activityInstance.SetActivity(script);

        // Act
        var nextActivities = await script.GetNextActivities(workflowInstance, activityInstance);

        // Assert
        Assert.HasCount(1, nextActivities);
        Assert.AreEqual("end", nextActivities[0].ActivityId);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldMarkActivityAsExecuting()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.x = 10");
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, script, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, script),
                new SequenceFlow("seq2", script, end)
            }
        };

        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        var state = await workflowInstance.GetState();
        var activeActivities = await state.GetActiveActivities();
        var taskActivity = activeActivities.FirstOrDefault();

        if (taskActivity != null)
        {
            // Act
            await script.ExecuteAsync(workflowInstance, taskActivity);

            // Assert
            Assert.IsTrue(await taskActivity.IsExecuting());
        }
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
