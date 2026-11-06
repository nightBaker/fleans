using Fleans.Application.Bpmn;
using Fleans.Application.CustomTasks;
using Fleans.Application.Deployments;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.Deployments;

[TestClass]
public class CustomTaskAccessValidatorTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.AddSiloBuilderConfigurator<StubConverterConfigurator>();

    private ICustomTaskCatalogGrain Catalog => Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static WorkflowDefinition Workflow(string key, Activity task)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = key,
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)],
        };
    }

    private Task RegisterPrivate(string taskType, params string[] allowed) =>
        Catalog.Register(new CustomTaskRegistration(taskType, null, null, "silo", null, CustomTaskVisibility.Private, [.. allowed]));

    private Task<Fleans.Application.QueryModels.ProcessDefinitionSummary> Deploy(WorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId).DeployVersion(workflow, "<bpmn/>");

    [TestMethod]
    public void CollectTaskTypes_FindsTasksInSubProcessesAndMultiInstance()
    {
        // Arrange
        var inSubProcess = new SubProcess("sub")
        {
            Activities = [new CustomTaskActivity("nested", "in-sub-process")],
            SequenceFlows = [],
        };
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "p",
            Activities =
            [
                new CustomTaskActivity("top", "top-level"),
                new CustomTaskActivity("top2", "TOP-LEVEL"),
                new MultiInstanceActivity("mi", new CustomTaskActivity("inner", "multi-instance"), LoopCardinality: 2),
                inSubProcess,
            ],
            SequenceFlows = [],
        };

        // Act
        var taskTypes = CustomTaskAccessValidator.CollectTaskTypes(workflow);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "top-level", "multi-instance", "in-sub-process" }, taskTypes);
    }

    [TestMethod]
    public async Task Deploy_DisallowedProcess_IsRejectedNamingProcessAndPlugin()
    {
        // Arrange
        var taskType = Unique("private");
        var key = Unique("not-allowed");
        await RegisterPrivate(taskType, "someone-else");

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            Deploy(Workflow(key, new CustomTaskActivity("task", taskType))));

        // Assert
        StringAssert.Contains(ex.Message, key);
        StringAssert.Contains(ex.Message, taskType);
        Assert.AreEqual(0, await QueryService.GetMaxVersionByKeyAsync(key), "a rejected deployment stores no version");
    }

    [TestMethod]
    public async Task Deploy_DisallowedTaskInsideMultiInstance_IsRejected()
    {
        // Arrange
        var taskType = Unique("private");
        await RegisterPrivate(taskType);

        // Act + Assert
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Deploy(Workflow(Unique("not-allowed"),
            new MultiInstanceActivity("mi", new CustomTaskActivity("inner", taskType), LoopCardinality: 2))));
    }

    [TestMethod]
    public async Task Deploy_AllowedProcessOrPublicPluginOrUnknownType_Succeeds()
    {
        // Arrange
        var key = Unique("allowed");
        var privateType = Unique("private");
        var publicType = Unique("public");
        await RegisterPrivate(privateType, key);
        await Catalog.Register(new CustomTaskRegistration(publicType, null, null, "silo"));

        // Act
        var allowed = await Deploy(Workflow(key, new CustomTaskActivity("task", privateType)));
        var open = await Deploy(Workflow(Unique("any"), new CustomTaskActivity("task", publicType)));
        var unknown = await Deploy(Workflow(Unique("any"), new CustomTaskActivity("task", Unique("nobody-claims"))));

        // Assert
        Assert.AreEqual(1, allowed.Version);
        Assert.AreEqual(1, open.Version);
        Assert.AreEqual(1, unknown.Version);
    }

    [TestMethod]
    public async Task AllowListEdit_AppliesToTheNextDeploy()
    {
        // Arrange
        var key = Unique("late");
        var taskType = Unique("private");
        await RegisterPrivate(taskType);
        var workflow = Workflow(key, new CustomTaskActivity("task", taskType));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Deploy(workflow));

        // Act
        await Catalog.SetAllowedProcesses(taskType, [key]);
        var deployed = await Deploy(workflow);

        // Assert
        Assert.AreEqual(1, deployed.Version);
    }

    [TestMethod]
    public async Task AllowListEdit_DoesNotAffectRunningInstancesOrDeployedVersions()
    {
        // Arrange — a version deployed and running while the process was allowed
        var key = Unique("running");
        var taskType = Unique("private");
        await RegisterPrivate(taskType, key);
        var workflow = Workflow(key, new CustomTaskActivity("task", taskType));
        await Deploy(workflow);
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(key);
        var definition = await processGrain.GetLatestDefinition();
        var running = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await running.StartWorkflow(definition);

        // Act — the process loses its access
        await Catalog.SetAllowedProcesses(taskType, []);
        await running.CompleteActivity("task", new System.Dynamic.ExpandoObject());

        // Assert — the running instance finishes, the deployed version still starts, a new one is rejected
        var snapshot = await WaitForCondition(running.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        var fresh = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await fresh.StartWorkflow(await processGrain.GetLatestDefinition());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Deploy(workflow));
    }

    [TestMethod]
    public async Task SubmitDeploymentRequest_Disallowed_IsRejectedBeforeReview()
    {
        // Arrange
        var key = Unique("request");
        await RegisterPrivate(StubBpmnConverter.TaskType);

        // Act
        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            Cluster.GrainFactory.GetGrain<IDeploymentRequestGrain>(Guid.NewGuid())
                .Submit(key, "alice", null, requiredApprovals: 1, deployDisabled: false, reviewerGroups: []));

        // Assert
        StringAssert.Contains(ex.Message, StubBpmnConverter.TaskType);
    }

    [TestMethod]
    public async Task ApprovedRequest_ProcessLostAccessMeanwhile_RecordsDeployErrorAndCanBeRetried()
    {
        // Arrange — submitted while allowed, then the allow-list changes before approval
        var key = Unique("request");
        await RegisterPrivate(StubBpmnConverter.TaskType, key);
        var grain = Cluster.GrainFactory.GetGrain<IDeploymentRequestGrain>(Guid.NewGuid());
        await grain.Submit(key, "alice", null, requiredApprovals: 1, deployDisabled: false, reviewerGroups: []);
        await Catalog.SetAllowedProcesses(StubBpmnConverter.TaskType, []);

        // Act
        var approved = await grain.Approve("bob", [], null);

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Approved, approved.Status);
        StringAssert.Contains(approved.DeployError, StubBpmnConverter.TaskType);

        // Act + Assert — the retry succeeds once the process is allowed again
        await Catalog.SetAllowedProcesses(StubBpmnConverter.TaskType, [key]);
        var retried = await grain.RetryDeployment();
        Assert.AreEqual(DeploymentRequestStatus.Deployed, retried.Status);
    }

    private class StubConverterConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder.ConfigureServices(services => services.AddSingleton<IBpmnConverter, StubBpmnConverter>());
    }

    /// <summary>The "XML" is the process key; the workflow is start → one custom task → end.</summary>
    private sealed class StubBpmnConverter : IBpmnConverter
    {
        public const string TaskType = "request-private-task";

        public async Task<WorkflowDefinition> ConvertFromXmlAsync(Stream bpmnXmlStream)
        {
            using var reader = new StreamReader(bpmnXmlStream);
            return Workflow(await reader.ReadToEndAsync(), new CustomTaskActivity("task", TaskType));
        }
    }
}
