using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

/// <summary>
/// Every end-event definition either maps to its own activity type or is rejected at deploy.
/// Falling through to a plain <see cref="EndEvent"/> runs a different process than the one drawn.
/// </summary>
[TestClass]
public class EndEventVariantParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    [DataRow("<errorEventDefinition errorRef=\"err1\" />")]
    [DataRow("<terminateEventDefinition />")]
    [DataRow("<signalEventDefinition signalRef=\"sig1\" />")]
    [DataRow("<messageEventDefinition messageRef=\"msg1\" />")]
    [Ignore("Blocked by #843: error, terminate, signal and message end events fall through to a plain EndEvent.")]
    public async Task EndEventVariant_IsParsedAsItsOwnTypeOrRejected(string definition)
    {
        // Arrange
        var bpmnXml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <error id=""err1"" errorCode=""E1"" />
  <signal id=""sig1"" name=""s1"" />
  <message id=""msg1"" name=""m1"" />
  <process id=""process1"">
    <startEvent id=""start"" />
    <endEvent id=""variantEnd"">{definition}</endEvent>
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""variantEnd"" />
  </process>
</definitions>";

        // Act
        Domain.IWorkflowDefinition workflow;
        try
        {
            workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        }
        catch (InvalidOperationException ex)
        {
            // Assert — rejected, naming the element
            StringAssert.Contains(ex.Message, "variantEnd");
            return;
        }

        // Assert — or parsed as something other than a plain end event
        var parsed = workflow.Activities.Single(a => a.ActivityId == "variantEnd");
        Assert.AreNotEqual(typeof(EndEvent), parsed.GetType(), $"{definition} must not run as a plain end event");
    }
}
