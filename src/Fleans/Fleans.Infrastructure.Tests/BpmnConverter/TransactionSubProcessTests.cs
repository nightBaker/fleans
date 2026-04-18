using Fleans.Domain.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class TransactionSubProcessTests
{
    private readonly Fleans.Infrastructure.Bpmn.BpmnConverter _converter =
        new(NullLogger<Fleans.Infrastructure.Bpmn.BpmnConverter>.Instance);

    private static string MakeBpmn(string body) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                      id="def1" targetNamespace="test">
           <process id="proc1" isExecutable="true">
             <startEvent id="start" />
             {body}
             <endEvent id="end" />
           </process>
         </definitions>
         """;

    private async Task<Fleans.Domain.WorkflowDefinition> ParseBpmn(string bpmn)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(bpmn));
        return await _converter.ConvertFromXmlAsync(stream);
    }

    [TestMethod]
    public async Task Transaction_BasicParsing_ProducesTransactionRecord()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <startEvent id="tx_start" />
              <scriptTask id="tx_task" scriptFormat="csharp"><script>_context.x = 1</script></scriptTask>
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_task" />
              <sequenceFlow id="tsf2" sourceRef="tx_task" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        var workflow = await ParseBpmn(bpmn);

        var tx = workflow.Activities.OfType<Transaction>().FirstOrDefault();
        Assert.IsNotNull(tx, "Transaction should be parsed as a Transaction record");
        Assert.AreEqual("tx1", tx.ActivityId);
    }

    [TestMethod]
    public async Task Transaction_ContainsChildActivities()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <startEvent id="tx_start" />
              <scriptTask id="tx_task" scriptFormat="csharp"><script>_context.x = 1</script></scriptTask>
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_task" />
              <sequenceFlow id="tsf2" sourceRef="tx_task" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        var workflow = await ParseBpmn(bpmn);

        var tx = workflow.Activities.OfType<Transaction>().First();
        Assert.IsTrue(tx.Activities.Any(a => a.ActivityId == "tx_start"), "tx_start should be inside Transaction");
        Assert.IsTrue(tx.Activities.Any(a => a.ActivityId == "tx_task"), "tx_task should be inside Transaction");
        Assert.IsTrue(tx.Activities.Any(a => a.ActivityId == "tx_end"), "tx_end should be inside Transaction");
    }

    [TestMethod]
    public async Task Transaction_ChildActivitiesNotInRootScope()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <startEvent id="tx_start" />
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        var workflow = await ParseBpmn(bpmn);

        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "tx_start"),
            "tx_start must NOT appear at root level");
        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "tx_end"),
            "tx_end must NOT appear at root level");
    }

    [TestMethod]
    public async Task Transaction_InternalSequenceFlows_AreContained()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <startEvent id="tx_start" />
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        var workflow = await ParseBpmn(bpmn);

        var tx = workflow.Activities.OfType<Transaction>().First();
        Assert.AreEqual(1, tx.SequenceFlows.Count, "Transaction should contain 1 internal flow");

        Assert.IsTrue(workflow.SequenceFlows.All(sf => sf.Source.ActivityId != "tx_start"),
            "Root flows must not contain Transaction-internal flows");
    }

    [TestMethod]
    public async Task Transaction_IsSubProcess_CanBeUsedAsScopeHost()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <startEvent id="tx_start" />
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        var workflow = await ParseBpmn(bpmn);

        var tx = workflow.Activities.OfType<Transaction>().First();
        Assert.IsInstanceOfType<SubProcess>(tx, "Transaction must inherit from SubProcess");
    }

    [TestMethod]
    public async Task Transaction_NestedImmediately_ThrowsInvalidOperation()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx_outer">
              <startEvent id="outer_start" />
              <transaction id="tx_inner">
                <startEvent id="inner_start" />
                <endEvent id="inner_end" />
                <sequenceFlow id="inner_sf1" sourceRef="inner_start" targetRef="inner_end" />
              </transaction>
              <endEvent id="outer_end" />
              <sequenceFlow id="osf1" sourceRef="outer_start" targetRef="tx_inner" />
              <sequenceFlow id="osf2" sourceRef="tx_inner" targetRef="outer_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx_outer" />
            <sequenceFlow id="f2" sourceRef="tx_outer" targetRef="end" />
            """);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ParseBpmn(bpmn),
            "Nested Transaction Sub-Process should throw InvalidOperationException");
    }

    [TestMethod]
    public async Task Transaction_NestedInsidePlainSubProcess_IsValid()
    {
        var bpmn = MakeBpmn("""
            <subProcess id="outer_sp">
              <startEvent id="sp_start" />
              <transaction id="tx_inner">
                <startEvent id="inner_start" />
                <endEvent id="inner_end" />
                <sequenceFlow id="inner_sf1" sourceRef="inner_start" targetRef="inner_end" />
              </transaction>
              <endEvent id="sp_end" />
              <sequenceFlow id="spsf1" sourceRef="sp_start" targetRef="tx_inner" />
              <sequenceFlow id="spsf2" sourceRef="tx_inner" targetRef="sp_end" />
            </subProcess>
            <sequenceFlow id="f1" sourceRef="start" targetRef="outer_sp" />
            <sequenceFlow id="f2" sourceRef="outer_sp" targetRef="end" />
            """);

        // tx_inner is inside a subProcess which is inside the root process
        // Since subProcess uses insideTransaction=false, tx_inner is not nested in a transaction
        // This should NOT throw — it's a subProcess containing a transaction, which is valid
        var workflow = await ParseBpmn(bpmn);
        var outerSp = workflow.Activities.OfType<SubProcess>()
            .FirstOrDefault(s => s.ActivityId == "outer_sp" && s is not Transaction);
        Assert.IsNotNull(outerSp, "Outer subProcess should be parsed");
        Assert.IsTrue(outerSp.Activities.OfType<Transaction>().Any(),
            "Transaction inside subProcess should be valid");
    }

    [TestMethod]
    public async Task Transaction_NestedInsideTransaction_ViaSubProcess_ThrowsInvalidOperation()
    {
        // transaction > subProcess > transaction — should fail because insideTransaction=true propagates
        var bpmn = MakeBpmn("""
            <transaction id="tx_outer">
              <startEvent id="outer_start" />
              <subProcess id="sp_mid">
                <startEvent id="mid_start" />
                <transaction id="tx_inner">
                  <startEvent id="inner_start" />
                  <endEvent id="inner_end" />
                  <sequenceFlow id="inner_sf1" sourceRef="inner_start" targetRef="inner_end" />
                </transaction>
                <endEvent id="mid_end" />
                <sequenceFlow id="midsf1" sourceRef="mid_start" targetRef="tx_inner" />
                <sequenceFlow id="midsf2" sourceRef="tx_inner" targetRef="mid_end" />
              </subProcess>
              <endEvent id="outer_end" />
              <sequenceFlow id="osf1" sourceRef="outer_start" targetRef="sp_mid" />
              <sequenceFlow id="osf2" sourceRef="sp_mid" targetRef="outer_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx_outer" />
            <sequenceFlow id="f2" sourceRef="tx_outer" targetRef="end" />
            """);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ParseBpmn(bpmn),
            "Transaction nested inside a Transaction (via subProcess) should throw");
    }

    [TestMethod]
    public async Task Transaction_WithMultiInstanceLoopCharacteristics_ThrowsInvalidOperation()
    {
        var bpmn = MakeBpmn("""
            <transaction id="tx1">
              <multiInstanceLoopCharacteristics isSequential="false">
                <loopCardinality>3</loopCardinality>
              </multiInstanceLoopCharacteristics>
              <startEvent id="tx_start" />
              <endEvent id="tx_end" />
              <sequenceFlow id="tsf1" sourceRef="tx_start" targetRef="tx_end" />
            </transaction>
            <sequenceFlow id="f1" sourceRef="start" targetRef="tx1" />
            <sequenceFlow id="f2" sourceRef="tx1" targetRef="end" />
            """);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ParseBpmn(bpmn),
            "Multi-instance Transaction Sub-Process should throw InvalidOperationException");
    }
}
