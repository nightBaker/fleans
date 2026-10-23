using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests.Deployments;

[TestClass]
public class VersionFingerprintTests : WorkflowTestBase
{
    private async Task Deploy(string key, string bpmnXml)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = key,
            Activities = new List<Activity> { start, end },
            SequenceFlows = new List<SequenceFlow> { new SequenceFlow("seq1", start, end) },
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(key).DeployVersion(workflow, bpmnXml);
    }

    [TestMethod]
    public async Task GetVersionFingerprints_HashesEachVersionsBpmn_NewestFirst()
    {
        // Arrange — v1 and v3 carry the same diagram
        await Deploy("fp-process", "<a/>");
        await Deploy("fp-process", "<b/>");
        await Deploy("fp-process", "<a/>");
        await Deploy("fp-other", "<c/>");

        // Act
        var all = await QueryService.GetVersionFingerprints(["fp-process", "fp-unknown"], latestOnly: false);

        // Assert
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, all.Select(f => f.Version).ToArray());
        Assert.AreEqual(all[0].BpmnSha256, all[2].BpmnSha256);
        Assert.AreNotEqual(all[0].BpmnSha256, all[1].BpmnSha256);
        Assert.AreEqual(64, all[0].BpmnSha256.Length);
    }

    [TestMethod]
    public async Task GetVersionFingerprints_LatestOnly_ReturnsOneRowPerKey()
    {
        // Arrange
        await Deploy("fp-latest-a", "<a1/>");
        await Deploy("fp-latest-a", "<a2/>");
        await Deploy("fp-latest-b", "<b1/>");

        // Act
        var latest = await QueryService.GetVersionFingerprints(["fp-latest-a", "fp-latest-b"], latestOnly: true);

        // Assert
        CollectionAssert.AreEqual(new[] { ("fp-latest-a", 2), ("fp-latest-b", 1) },
            latest.Select(f => (f.ProcessDefinitionKey, f.Version)).ToArray());
    }
}
