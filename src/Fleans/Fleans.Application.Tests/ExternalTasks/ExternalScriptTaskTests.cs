using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.ExternalTasks.Sdk;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// Script tasks in a format other than C# (#1105) run on the worker plugin of
/// <c>fleans.script.&lt;format&gt;</c>: it gets the script text as the <c>script</c> input and the
/// variables, and what it returns merges into the scope. A failure fails the activity with the
/// worker's code; a deploy without a connected worker succeeds with a warning.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExternalScriptTaskTests : WorkflowTestBase
{
    private const string TaskType = "fleans.script.javascript";

    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    [TestInitialize]
    public void ResetHandler() => FakeScriptHandler.Received.Clear();

    [TestMethod]
    public async Task A_js_script_task_runs_on_the_javascript_worker_and_merges_what_it_returns()
    {
        // Arrange
        await using var workers = await StartWorker();
        await Deploy("script-js-ok", "js", "double");

        // Act
        var instanceId = await CommandService.StartWorkflow("script-js-ok", Vars(("count", 21L), ("name", "kept")));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual("42", Variable(snapshot, "doubled"));
        Assert.AreEqual("kept", Variable(snapshot, "name"));
        var received = FakeScriptHandler.Received.Single();
        Assert.AreEqual(TaskType, received.TaskType);
        Assert.AreEqual("double", received.Script);
        CollectionAssert.IsSubsetOf(new[] { "count", "name" }, received.VariableNames);
        Assert.IsNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "script").ErrorState);
    }

    [TestMethod]
    public async Task A_throwing_script_fails_the_task_with_code_500_and_its_message()
    {
        // Arrange
        await using var workers = await StartWorker();
        await Deploy("script-js-throw", "javascript", "throw");

        // Act
        var instanceId = await CommandService.StartWorkflow("script-js-throw", Vars(("count", 1L)));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        var error = snapshot.CompletedActivities.Single(a => a.ActivityId == "script").ErrorState!;
        Assert.AreEqual("500", error.Code);
        StringAssert.Contains(error.Message, "script exploded");
        Assert.IsNull(Variable(snapshot, "doubled"));
    }

    [TestMethod]
    public async Task A_bpmn_error_code_from_the_worker_reaches_an_error_boundary()
    {
        // Arrange
        await using var workers = await StartWorker();
        var start = new StartEvent("start");
        var script = new ScriptTask("script", "bpmn-error", "javascript");
        var boundary = new BoundaryErrorEvent("onRejected", "script", "SCRIPT_REJECTED");
        var end = new EndEvent("end");
        var recovered = new EndEvent("recovered");
        await DeployDefinition(new WorkflowDefinition
        {
            WorkflowId = "script-js-boundary",
            Activities = [start, script, boundary, end, recovered],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, script),
                new SequenceFlow("f2", script, end),
                new SequenceFlow("f3", boundary, recovered),
            ]
        });

        // Act
        var instanceId = await CommandService.StartWorkflow("script-js-boundary", Vars(("count", 1L)));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("recovered"));
        Assert.AreEqual("SCRIPT_REJECTED",
            snapshot.CompletedActivities.Single(a => a.ActivityId == "script").ErrorState!.Code);
    }

    [TestMethod]
    public async Task A_deploy_without_a_connected_worker_succeeds_with_a_warning_and_the_task_waits()
    {
        // Arrange — no worker for fleans.script.python

        // Act
        var deployed = await Deploy("script-py-waits", "py", "double");
        var instanceId = await CommandService.StartWorkflow("script-py-waits", Vars(("count", 1L)));

        // Assert
        var warning = deployed.Warnings!.Single();
        StringAssert.Contains(warning, "'script'");
        StringAssert.Contains(warning, "fleans.script.python");
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "script"));
        Assert.IsFalse(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task A_deploy_with_a_connected_worker_has_no_warning()
    {
        // Arrange
        await using var workers = await StartWorker();

        // Act
        var deployed = await Deploy("script-js-no-warning", "JavaScript", "double");

        // Assert
        Assert.IsEmpty(deployed.Warnings!);
    }

    private async Task<GrpcExternalTaskWorkers> StartWorker()
    {
        var workers = new GrpcExternalTaskWorkers(Cluster.Client, TestWorkerHeartbeatInterval, TestExternalTaskExecutionTimeout);
        await workers.StartAsync("js-worker", sv => sv.AddExternalTaskHandler<FakeScriptHandler>(
            TaskType, variables: ExternalTaskVariables.All));
        await workers.WaitForCatalogWorkers(TaskType, "js-worker");
        return workers;
    }

    private Task<ProcessDefinitionSummary> Deploy(string workflowId, string format, string scriptText)
    {
        var start = new StartEvent("start");
        var script = new ScriptTask("script", scriptText, format);
        var end = new EndEvent("end");
        return DeployDefinition(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, script, end],
            SequenceFlows = [new SequenceFlow("f1", start, script), new SequenceFlow("f2", script, end)]
        });
    }

    private Task<ProcessDefinitionSummary> DeployDefinition(WorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId).DeployVersion(workflow, "<placeholder/>");

    private static string? Variable(QueryModels.InstanceStateSnapshot snapshot, string name) =>
        snapshot.VariableStates.SelectMany(v => v.Variables).FirstOrDefault(kv => kv.Key == name).Value;

    private static ExpandoObject Vars(params (string Key, object Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in values)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    private sealed record Delivery(string TaskType, string? Script, List<string> VariableNames);

    /// <summary>
    /// Stands in for a script engine: <c>double</c> returns <c>doubled = count * 2</c> (and nothing
    /// else, as a real plugin returns only what the script changed), <c>throw</c> throws like a
    /// failing script, <c>bpmn-error</c> fails with a BPMN error code.
    /// </summary>
    private sealed class FakeScriptHandler : ExternalTaskHandler
    {
        public static readonly ConcurrentQueue<Delivery> Received = new();

        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            var script = resolvedInputs[ScriptFormats.ScriptInput]?.ToString();
            var vars = (IDictionary<string, object?>)variables;
            Received.Enqueue(new Delivery(context.TaskType, script, vars.Keys.ToList()));

            return script switch
            {
                "throw" => throw new InvalidOperationException("script exploded"),
                "bpmn-error" => throw new ExternalTaskFailedException("SCRIPT_REJECTED", "rejected by the script"),
                _ => Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
                {
                    ["doubled"] = Convert.ToInt64(vars["count"]) * 2,
                }),
            };
        }
    }
}
