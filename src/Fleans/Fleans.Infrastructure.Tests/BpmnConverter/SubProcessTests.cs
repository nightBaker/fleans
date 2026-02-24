using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class SubProcessTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSubProcess()
    {
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var subProcess = workflow.Activities.OfType<SubProcess>().FirstOrDefault(s => s.ActivityId == "sub1");
        Assert.IsNotNull(subProcess, "SubProcess should be parsed");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_SubProcess_ShouldContainChildActivities()
    {
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var subProcess = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "sub1");
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_start"));
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_task"));
        Assert.IsTrue(subProcess.Activities.Any(a => a.ActivityId == "sub1_end"));

        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "sub1_start"),
            "Root should not contain sub-process children");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_SubProcess_ShouldContainInternalFlows()
    {
        var bpmnXml = CreateBpmnWithSubProcess("sp-workflow", "sub1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var subProcess = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "sub1");
        Assert.AreEqual(2, subProcess.SequenceFlows.Count);

        Assert.IsTrue(workflow.SequenceFlows.All(sf =>
            sf.Source.ActivityId != "sub1_start" && sf.Source.ActivityId != "sub1_task"),
            "Root flows should not contain sub-process internal flows");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_NestedSubProcess_ShouldParseRecursively()
    {
        var bpmnXml = CreateBpmnWithNestedSubProcess("nested-workflow");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var outer = workflow.Activities.OfType<SubProcess>().First(s => s.ActivityId == "outer");
        var inner = outer.Activities.OfType<SubProcess>().FirstOrDefault(s => s.ActivityId == "inner");
        Assert.IsNotNull(inner, "Nested SubProcess should be parsed inside outer");
        Assert.IsTrue(inner.Activities.Any(a => a.ActivityId == "inner_task"));
    }
}
