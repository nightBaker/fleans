using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

/// <summary>
/// Models that are invalid BPMN, or that the engine cannot run as drawn, are rejected at deploy with a message
/// naming the element, rather than deployed and misbehaving at run time.
/// </summary>
[TestClass]
public class InvalidModelParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    [DataRow("mixed-inclusive-gateway",
        """
        <startEvent id="start" />
        <parallelGateway id="fork" />
        <task id="a" /><task id="b" /><task id="c" />
        <inclusiveGateway id="mixed" />
        <endEvent id="end1" /><endEvent id="end2" />
        <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
        <sequenceFlow id="f2" sourceRef="fork" targetRef="a" />
        <sequenceFlow id="f3" sourceRef="fork" targetRef="b" />
        <sequenceFlow id="f4" sourceRef="a" targetRef="mixed" />
        <sequenceFlow id="f5" sourceRef="b" targetRef="mixed" />
        <sequenceFlow id="f6" sourceRef="mixed" targetRef="end1"><conditionExpression>true</conditionExpression></sequenceFlow>
        <sequenceFlow id="f7" sourceRef="mixed" targetRef="end2"><conditionExpression>false</conditionExpression></sequenceFlow>
        """,
        "mixed")]
    [DataRow("cancel-boundary-outside-a-transaction",
        """
        <startEvent id="start" />
        <subProcess id="sub"><startEvent id="subStart" /><endEvent id="subEnd" /><sequenceFlow id="s1" sourceRef="subStart" targetRef="subEnd" /></subProcess>
        <boundaryEvent id="onCancel" attachedToRef="sub"><cancelEventDefinition /></boundaryEvent>
        <endEvent id="end" /><endEvent id="cancelled" />
        <sequenceFlow id="f1" sourceRef="start" targetRef="sub" />
        <sequenceFlow id="f2" sourceRef="sub" targetRef="end" />
        <sequenceFlow id="f3" sourceRef="onCancel" targetRef="cancelled" />
        """,
        "onCancel")]
    [DataRow("unsupported-event-definition",
        """
        <startEvent id="start" />
        <intermediateCatchEvent id="waitLink"><linkEventDefinition name="L1" /></intermediateCatchEvent>
        <endEvent id="end" />
        <sequenceFlow id="f1" sourceRef="start" targetRef="waitLink" />
        <sequenceFlow id="f2" sourceRef="waitLink" targetRef="end" />
        """,
        "waitLink")]
    [DataRow("invalid-cancel-activity",
        """
        <startEvent id="start" />
        <task id="work" />
        <boundaryEvent id="timeout" attachedToRef="work" cancelActivity="maybe"><timerEventDefinition><timeDuration>PT1H</timeDuration></timerEventDefinition></boundaryEvent>
        <endEvent id="end" /><endEvent id="late" />
        <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
        <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
        <sequenceFlow id="f3" sourceRef="timeout" targetRef="late" />
        """,
        "timeout")]
    [DataRow("unknown-escalation-ref",
        """
        <startEvent id="start" />
        <intermediateThrowEvent id="escalate"><escalationEventDefinition escalationRef="noSuchEscalation" /></intermediateThrowEvent>
        <endEvent id="end" />
        <sequenceFlow id="f1" sourceRef="start" targetRef="escalate" />
        <sequenceFlow id="f2" sourceRef="escalate" targetRef="end" />
        """,
        "noSuchEscalation")]
    public async Task InvalidModel_IsRejectedNamingTheCulprit(string name, string processBody, string named)
    {
        // Arrange
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
              <process id="{name}">
            {processBody}
              </process>
            </definitions>
            """;

        // Act
        var error = await Rejection(xml);

        // Assert
        Assert.IsNotNull(error, $"{name}: the model deploys");
        Assert.Contains(named, error.Message, $"{name}: the message names the element or the missing reference");
    }

    private async Task<Exception?> Rejection(string xml)
    {
        try
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
