using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

/// <summary>
/// Elements the engine does not implement as drawn must be rejected at deploy (or mapped to an activity that does
/// implement them), never dropped or deployed as something that behaves differently. The implicit split from a
/// task with two outgoing flows (#856) is pinned in <c>Fleans.Application.Tests/ImplicitSplitTests.cs</c>.
/// </summary>
[TestClass]
public class UnsupportedElementParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    [DataRow("<sendTask id=\"x\" />")]
    [DataRow("<receiveTask id=\"x\" messageRef=\"m1\" />")]
    [DataRow("<manualTask id=\"x\" />")]
    [DataRow("<businessRuleTask id=\"x\" />")]
    [Ignore("Blocked by #1125: send, receive, manual and business-rule tasks are dropped together with their sequence flows.")]
    public async Task TaskType_IsKeptOrRejected_NeverDropped(string element)
    {
        // Act
        var (workflow, error) = await Parse(element);

        // Assert
        if (error is not null)
        {
            Assert.Contains("x", error.Message, "a rejection names the element");
            return;
        }
        Assert.IsTrue(workflow!.Activities.Any(a => a.ActivityId == "x"), $"{element} is part of the deployed process");
        Assert.HasCount(2, workflow.SequenceFlows, "its flows are kept");
    }

    [TestMethod]
    [DataRow("<task id=\"x\"><standardLoopCharacteristics /></task>")]
    [DataRow("<userTask id=\"x\"><standardLoopCharacteristics testBefore=\"true\"><loopCondition>true</loopCondition></standardLoopCharacteristics></userTask>")]
    [Ignore("Blocked by #1125: standardLoopCharacteristics is ignored, so the activity deploys as a plain one that runs once.")]
    public async Task StandardLoop_IsRejected_UntilItIsImplemented(string element)
    {
        // Act
        var (_, error) = await Parse(element);

        // Assert
        Assert.IsNotNull(error, $"{element} must not deploy as an activity that runs once");
    }

    private async Task<(Domain.IWorkflowDefinition? Workflow, Exception? Error)> Parse(string element)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <message id="m1" name="m1" />
              <process id="p">
                <startEvent id="start" />
                {element}
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="x" />
                <sequenceFlow id="f2" sourceRef="x" targetRef="end" />
              </process>
            </definitions>
            """;
        try
        {
            return (await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml))), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }
}
