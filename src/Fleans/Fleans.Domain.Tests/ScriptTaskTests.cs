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
    public void ScriptTask_ShouldThrowOnNullScript()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScriptTask("script1", null!));
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

        Assert.IsNotNull(taskActivity, "Workflow should have an active ScriptTask activity instance");

        // Act
        await script.ExecuteAsync(workflowInstance, taskActivity);

        // Assert
        Assert.IsTrue(await taskActivity.IsExecuting());
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldTransitionToEndEvent_AndCompleteWorkflow()
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

        dynamic variables = new ExpandoObject();
        variables.x = 10;

        // Act
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)variables);

        // Assert
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedActivities = await state.GetCompletedActivities();
        var completedActivityIds = new List<string>();
        foreach (var activity in completedActivities)
        {
            var current = await activity.GetCurrentActivity();
            completedActivityIds.Add(current.ActivityId);
        }

        CollectionAssert.Contains(completedActivityIds, "start");
        CollectionAssert.Contains(completedActivityIds, "script1");
        CollectionAssert.Contains(completedActivityIds, "end");
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldMergeVariablesIntoState()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.result = 42");
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

        dynamic variables = new ExpandoObject();
        variables.result = 42;
        variables.message = "done";

        // Act
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)variables);

        // Assert
        var state = await workflowInstance.GetState();
        var variableStates = await state.GetVariableStates();
        Assert.IsNotEmpty(variableStates);

        var mergedVariables = variableStates.Values.First().Variables as IDictionary<string, object>;
        Assert.IsNotNull(mergedVariables);
        Assert.AreEqual(2, mergedVariables.Count);
        Assert.AreEqual(42, mergedVariables["result"]);
        Assert.AreEqual("done", mergedVariables["message"]);
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldMarkActivityInstanceAsCompleted()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.x = 1");
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

        // Act
        await workflowInstance.CompleteActivity("script1", new ExpandoObject());

        // Assert — script task should appear in completed activities
        var state = await workflowInstance.GetState();
        var completedActivities = await state.GetCompletedActivities();
        var completedScriptTask = false;
        foreach (var activity in completedActivities)
        {
            var current = await activity.GetCurrentActivity();
            if (current.ActivityId == "script1")
            {
                Assert.IsTrue(await activity.IsCompleted());
                completedScriptTask = true;
            }
        }

        Assert.IsTrue(completedScriptTask, "ScriptTask should be in completed activities");
    }

    [TestMethod]
    public async Task CompleteScriptTask_ShouldHaveNoActiveActivities_AfterWorkflowCompletes()
    {
        // Arrange
        var script = new ScriptTask("script1", "_context.x = 1");
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

        // Act
        await workflowInstance.CompleteActivity("script1", new ExpandoObject());

        // Assert
        var state = await workflowInstance.GetState();
        var activeActivities = await state.GetActiveActivities();
        Assert.HasCount(0, activeActivities);
    }

    [TestMethod]
    public async Task CompleteScriptTask_WithMultipleScriptTasks_ShouldExecuteInSequence()
    {
        // Arrange
        var script1 = new ScriptTask("script1", "_context.step = 1");
        var script2 = new ScriptTask("script2", "_context.step = 2");
        var start = new StartEvent("start");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = new List<Activity> { start, script1, script2, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, script1),
                new SequenceFlow("seq2", script1, script2),
                new SequenceFlow("seq3", script2, end)
            }
        };

        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        dynamic vars1 = new ExpandoObject();
        vars1.step = 1;

        // Act — complete first script task
        await workflowInstance.CompleteActivity("script1", (ExpandoObject)vars1);

        // Assert — second script task should now be active
        var state = await workflowInstance.GetState();
        Assert.IsFalse(await state.IsCompleted());
        var activeActivities = await state.GetActiveActivities();
        Assert.HasCount(1, activeActivities);
        var activeActivity = await activeActivities[0].GetCurrentActivity();
        Assert.IsInstanceOfType(activeActivity, typeof(ScriptTask));
        Assert.AreEqual("script2", activeActivity.ActivityId);

        // Act — complete second script task
        dynamic vars2 = new ExpandoObject();
        vars2.step = 2;
        await workflowInstance.CompleteActivity("script2", (ExpandoObject)vars2);

        // Assert — workflow should be completed
        state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());
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
