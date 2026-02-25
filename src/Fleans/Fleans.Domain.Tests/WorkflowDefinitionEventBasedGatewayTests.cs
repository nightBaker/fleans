using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowDefinitionEventBasedGatewayTests
{
    [TestMethod]
    public void GetEventBasedGatewaySiblings_ActivityAfterEBG_ReturnsSiblingIds()
    {
        // Arrange
        var start = new StartEvent("start");
        var ebg = new EventBasedGateway("ebg");
        var timer1 = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var msg1 = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var sig1 = new SignalIntermediateCatchEvent("sig1", "sigDef1");
        var end = new EndEvent("end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [start, ebg, timer1, msg1, sig1, end],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, ebg),
                new SequenceFlow("sf2", ebg, timer1),
                new SequenceFlow("sf3", ebg, msg1),
                new SequenceFlow("sf4", ebg, sig1),
                new SequenceFlow("sf5", timer1, end),
            ]
        };

        // Act
        IWorkflowDefinition def = definition;
        var siblings = def.GetEventBasedGatewaySiblings("timer1");

        // Assert
        Assert.AreEqual(2, siblings.Count);
        Assert.IsTrue(siblings.Contains("msg1"));
        Assert.IsTrue(siblings.Contains("sig1"));
    }

    [TestMethod]
    public void GetEventBasedGatewaySiblings_ActivityNotAfterGateway_ReturnsEmptySet()
    {
        // Arrange
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, task),
                new SequenceFlow("sf2", task, end),
            ]
        };

        // Act
        IWorkflowDefinition def = definition;
        var siblings = def.GetEventBasedGatewaySiblings("task1");

        // Assert
        Assert.AreEqual(0, siblings.Count);
    }

    [TestMethod]
    public void GetEventBasedGatewaySiblings_MultipleSiblings_ReturnsAllExceptCompleted()
    {
        // Arrange
        var start = new StartEvent("start");
        var ebg = new EventBasedGateway("ebg");
        var a = new TimerIntermediateCatchEvent("a", new TimerDefinition(TimerType.Duration, "PT5S"));
        var b = new MessageIntermediateCatchEvent("b", "msgDef1");
        var c = new SignalIntermediateCatchEvent("c", "sigDef1");
        var end = new EndEvent("end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [start, ebg, a, b, c, end],
            SequenceFlows =
            [
                new SequenceFlow("sf1", start, ebg),
                new SequenceFlow("sf2", ebg, a),
                new SequenceFlow("sf3", ebg, b),
                new SequenceFlow("sf4", ebg, c),
                new SequenceFlow("sf5", a, end),
            ]
        };

        // Act
        IWorkflowDefinition def = definition;
        var siblings = def.GetEventBasedGatewaySiblings("a");

        // Assert
        Assert.AreEqual(2, siblings.Count);
        Assert.IsTrue(siblings.Contains("b"));
        Assert.IsTrue(siblings.Contains("c"));
    }

    [TestMethod]
    public void GetEventBasedGatewaySiblings_InsideSubProcess_ReturnsSiblings()
    {
        // Arrange
        var subStart = new StartEvent("sub-start");
        var ebg = new EventBasedGateway("ebg");
        var timer1 = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT10S"));
        var msg1 = new MessageIntermediateCatchEvent("msg1", "msgDef1");
        var subEnd = new EndEvent("sub-end");

        var subProcess = new SubProcess("sub")
        {
            Activities = [subStart, ebg, timer1, msg1, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("sub-sf1", subStart, ebg),
                new SequenceFlow("sub-sf2", ebg, timer1),
                new SequenceFlow("sub-sf3", ebg, msg1),
                new SequenceFlow("sub-sf4", timer1, subEnd),
            ]
        };

        var rootStart = new StartEvent("root-start");
        var rootEnd = new EndEvent("root-end");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "test",
            Activities = [rootStart, subProcess, rootEnd],
            SequenceFlows =
            [
                new SequenceFlow("sf1", rootStart, subProcess),
                new SequenceFlow("sf2", subProcess, rootEnd),
            ]
        };

        // Act — query from the root definition, which must recurse into SubProcess
        IWorkflowDefinition def = definition;
        var siblings = def.GetEventBasedGatewaySiblings("timer1");

        // Assert
        Assert.AreEqual(1, siblings.Count);
        Assert.IsTrue(siblings.Contains("msg1"));
    }
}
