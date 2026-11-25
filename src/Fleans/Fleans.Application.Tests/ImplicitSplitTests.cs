using Fleans.Application.Grains;
using Microsoft.Extensions.Logging.Abstractions;
using System.Dynamic;
using System.Text;

namespace Fleans.Application.Tests;

/// <summary>
/// A task with two outgoing sequence flows is an uncontrolled parallel split in BPMN 2.0. The engine
/// must either run both branches or refuse the model at deploy — never drop the second branch.
/// </summary>
[TestClass]
public class ImplicitSplitTests : WorkflowTestBase
{
    private const string TaskWithTwoOutgoingFlows = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""implicitSplit"" isExecutable=""true"">
    <startEvent id=""start"" />
    <task id=""split"" />
    <task id=""left"" />
    <task id=""right"" />
    <endEvent id=""leftEnd"" />
    <endEvent id=""rightEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""split"" />
    <sequenceFlow id=""f2"" sourceRef=""split"" targetRef=""left"" />
    <sequenceFlow id=""f3"" sourceRef=""split"" targetRef=""right"" />
    <sequenceFlow id=""f4"" sourceRef=""left"" targetRef=""leftEnd"" />
    <sequenceFlow id=""f5"" sourceRef=""right"" targetRef=""rightEnd"" />
  </process>
</definitions>";

    [TestMethod]
    [Ignore("Blocked by #856: a non-gateway activity follows only its first outgoing flow, so the second branch is silently dropped.")]
    public async Task TaskWithTwoOutgoingFlows_RunsBothBranches_OrIsRejectedAtDeploy()
    {
        // Arrange
        var converter = new Infrastructure.Bpmn.BpmnConverter(NullLogger<Infrastructure.Bpmn.BpmnConverter>.Instance);
        Domain.IWorkflowDefinition workflow;
        try
        {
            workflow = await converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(TaskWithTwoOutgoingFlows)));
        }
        catch (InvalidOperationException)
        {
            return; // Rejected at deploy: acceptable.
        }
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);

        // Act
        await instance.CompleteActivity("split", new ExpandoObject());

        // Assert — both branches are live
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("left"));
        CollectionAssert.Contains(snapshot.ActiveActivityIds, "right", "the second outgoing flow must not be dropped");
    }
}
