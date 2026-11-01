using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests.Deployments;

[TestClass]
public class DeployExpressionValidationTests : WorkflowTestBase
{
    [TestMethod]
    public async Task DeployVersion_UnparsableCondition_IsRejected_AndStoresNoVersion()
    {
        // Arrange
        const string key = "unparsable-condition";
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(key);

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => processGrain.DeployVersion(Workflow(key, UnparsableExpression), "<bpmn/>"));

        // Assert — the error names the flow; the fixed definition then deploys as version 1
        StringAssert.Contains(ex.Message, "sequence flow 'toEnd' condition");
        var deployed = await processGrain.DeployVersion(Workflow(key, "true"), "<bpmn/>");
        Assert.AreEqual(1, deployed.Version);
    }

    private static WorkflowDefinition Workflow(string key, string condition)
    {
        var start = new StartEvent("start");
        var gateway = new ExclusiveGateway("gateway");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = key,
            Activities = [start, gateway, end],
            SequenceFlows =
            [
                new SequenceFlow("toGateway", start, gateway),
                new ConditionalSequenceFlow("toEnd", gateway, end, condition)
            ]
        };
    }
}
