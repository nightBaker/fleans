using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Fleans.Application.Bpmn;
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
public class DeploymentRequestGrainTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder) =>
        builder.AddSiloBuilderConfigurator<StubConverterConfigurator>();

    private IDeploymentRequestGrain NewRequest() =>
        Cluster.GrainFactory.GetGrain<IDeploymentRequestGrain>(Guid.NewGuid());

    private static string Bpmn(string processId) =>
        $"""<definitions><process id="{processId}"><startEvent id="start"/></process></definitions>""";

    [TestMethod]
    public async Task Submit_WithNoApprovalsRequired_DeploysAtOnce()
    {
        // Arrange
        var key = "deploy-at-once";

        // Act
        var request = await NewRequest().Submit(Bpmn(key), "alice", "first version", requiredApprovals: 0, deployDisabled: false);

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Deployed, request.Status);
        Assert.AreEqual(1, request.DeployedVersion);
        Assert.AreEqual(1, await QueryService.GetMaxVersionByKeyAsync(key));
        Assert.IsTrue(await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(key).IsActive());
    }

    [TestMethod]
    public async Task Approve_DeploysOnlyAfterTheLastRequiredDistinctApproval()
    {
        // Arrange
        var key = "two-reviewers";
        var grain = NewRequest();
        await grain.Submit(Bpmn(key), "alice", "adds a timer", requiredApprovals: 2, deployDisabled: false);

        // Act
        var afterFirst = await grain.Approve("bob", "looks right");
        var versionsAfterFirst = await QueryService.GetMaxVersionByKeyAsync(key);
        var afterSecond = await grain.Approve("carol", null);

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Pending, afterFirst.Status);
        Assert.AreEqual(0, versionsAfterFirst, "Nothing may deploy before the last approval");
        Assert.AreEqual(DeploymentRequestStatus.Deployed, afterSecond.Status);
        CollectionAssert.AreEqual(new[] { "bob", "carol" }, afterSecond.Approvals.Select(a => a.UserId).ToArray());
        Assert.AreEqual(1, await QueryService.GetMaxVersionByKeyAsync(key));
    }

    [TestMethod]
    public async Task Approve_ByTheRequester_IsRefused_AndRecordsNothing()
    {
        // Arrange
        var key = "self-approval";
        var grain = NewRequest();
        await grain.Submit(Bpmn(key), "alice", null, requiredApprovals: 1, deployDisabled: false);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Approve("alice", null));

        // Assert
        var state = await grain.GetState();
        Assert.IsNotNull(state);
        Assert.AreEqual(DeploymentRequestStatus.Pending, state.Status);
        Assert.AreEqual(0, state.Approvals.Count);
        Assert.AreEqual(0, await QueryService.GetMaxVersionByKeyAsync(key));
    }

    [TestMethod]
    public async Task Reject_ClosesTheRequest_WithoutDeploying()
    {
        // Arrange
        var key = "rejected-process";
        var grain = NewRequest();
        await grain.Submit(Bpmn(key), "alice", null, requiredApprovals: 1, deployDisabled: false);

        // Act
        var rejected = await grain.Reject("bob", "no error boundary on the payment task");

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Rejected, rejected.Status);
        Assert.AreEqual("no error boundary on the payment task", rejected.CloseReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.Approve("carol", null));
        Assert.AreEqual(0, await QueryService.GetMaxVersionByKeyAsync(key));
    }

    [TestMethod]
    public async Task Submit_DeployDisabled_DeploysTheProcessDisabled()
    {
        // Arrange
        var key = "deploy-disabled";

        // Act
        var request = await NewRequest().Submit(Bpmn(key), "alice", null, requiredApprovals: 0, deployDisabled: true);

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Deployed, request.Status);
        Assert.IsFalse(await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(key).IsActive());
    }

    [TestMethod]
    public async Task Submit_InvalidBpmn_ThrowsArgumentException_AndStoresNothing()
    {
        // Arrange
        var grain = NewRequest();

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.Submit("<definitions/>", "alice", null, requiredApprovals: 1, deployDisabled: false));

        // Assert
        Assert.IsNull(await grain.GetState());
    }

    [TestMethod]
    public async Task Approve_WhenTheDeployFails_KeepsTheApproval_AndRetryDeploys()
    {
        // Arrange
        var key = "flaky-deploy";
        var grain = NewRequest();
        await grain.Submit(Bpmn(key), "alice", null, requiredApprovals: 1, deployDisabled: false);
        StubBpmnConverter.FailNextParses[key] = 1;

        // Act
        var failed = await grain.Approve("bob", null);
        var retried = await grain.RetryDeployment();

        // Assert
        Assert.AreEqual(DeploymentRequestStatus.Approved, failed.Status);
        Assert.IsNotNull(failed.DeployError);
        Assert.AreEqual(1, failed.Approvals.Count);
        Assert.AreEqual(DeploymentRequestStatus.Deployed, retried.Status);
        Assert.IsNull(retried.DeployError);
        Assert.AreEqual(1, await QueryService.GetMaxVersionByKeyAsync(key));
    }

    [TestMethod]
    public async Task Requests_AreListed_AndSurviveReactivation()
    {
        // Arrange
        var key = "listed-process";
        var id = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IDeploymentRequestGrain>(id);
        await grain.Submit(Bpmn(key), "alice", "please review", requiredApprovals: 2, deployDisabled: false);
        await grain.Approve("bob", null);

        // Act
        await Cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
        var reloaded = await grain.GetState();
        var pending = await QueryService.GetDeploymentRequests(DeploymentRequestStatus.Pending);
        var stored = await QueryService.GetDeploymentRequest(id);

        // Assert
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(1, reloaded.Approvals.Count);
        var row = pending.Single(r => r.Id == id);
        Assert.AreEqual(key, row.ProcessDefinitionKey);
        Assert.AreEqual("please review", row.Comment);
        Assert.AreEqual("bob", row.Approvals.Single().UserId);
        Assert.AreEqual(Bpmn(key), stored?.BpmnXml);
        Assert.AreEqual(0, (await QueryService.GetDeploymentRequests(DeploymentRequestStatus.Deployed)).Count(r => r.Id == id));
    }

    private class StubConverterConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder.ConfigureServices(services => services.AddSingleton<IBpmnConverter, StubBpmnConverter>());
    }

    /// <summary>Reads the process id from the XML and returns a start → task → end workflow.</summary>
    private sealed class StubBpmnConverter : IBpmnConverter
    {
        /// <summary>Process key → how many more parses of it fail (simulates a failing deploy).</summary>
        public static readonly ConcurrentDictionary<string, int> FailNextParses = new();

        public async Task<WorkflowDefinition> ConvertFromXmlAsync(Stream bpmnXmlStream)
        {
            using var reader = new StreamReader(bpmnXmlStream);
            var xml = await reader.ReadToEndAsync();
            var match = Regex.Match(xml, "<process id=\"([^\"]+)\"");
            if (!match.Success)
                throw new InvalidOperationException("No <process> element found.");

            var key = match.Groups[1].Value;
            if (FailNextParses.TryGetValue(key, out var failures) && failures > 0)
            {
                FailNextParses[key] = failures - 1;
                throw new InvalidOperationException("Simulated deploy failure");
            }

            var start = new StartEvent("start");
            var task = new TaskActivity("task");
            var end = new EndEvent("end");
            return new WorkflowDefinition
            {
                WorkflowId = key,
                Activities = new List<Activity> { start, task, end },
                SequenceFlows = new List<SequenceFlow>
                {
                    new SequenceFlow("seq1", start, task),
                    new SequenceFlow("seq2", task, end),
                },
            };
        }
    }
}
