using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class ScriptTaskTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithFormatAndScript()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script1", "script1", "csharp", "${result} = ${x} + ${y}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("script1", scriptTask.ActivityId);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
        Assert.AreEqual("_context.result = _context.x + _context.y", scriptTask.Script);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithDefaultFormat()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script2", "script1", null, "${total} = 42");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldNotConvertBareVariablesInScript()
    {
        // Arrange — script uses _context.var directly, bare identifiers like Math should not be converted
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script4", "script1", "csharp", "_context.x = 42");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — bare variable conversion should NOT be applied to scripts
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("_context.x = 42", scriptTask.Script);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithMissingScriptElement()
    {
        // Arrange — scriptTask with no <script> child should produce empty script
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow_script5"">
    <startEvent id=""start"" />
    <scriptTask id=""script1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""script1"" />
    <sequenceFlow id=""flow2"" sourceRef=""script1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("", scriptTask.Script);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldKeepJavaScriptVerbatim()
    {
        // Arrange — ${...} is template-literal syntax in JavaScript, not the BPMN variable shorthand
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script6", "script1", "javascript", "_context.msg = `Hi ${_context.name}`");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().Single();
        Assert.AreEqual("javascript", scriptTask.ScriptFormat);
        Assert.AreEqual("_context.msg = `Hi ${_context.name}`", scriptTask.Script);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldLeaveUnknownFormatToDeployValidation()
    {
        // Arrange — the parser does not know which script engines a silo registered
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script7", "script1", "groovy", "x = 1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.AreEqual("groovy", workflow.Activities.OfType<ScriptTask>().Single().ScriptFormat);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldDedentPythonKeepingItsBlocks()
    {
        // Arrange — #1102: the script is indented inside the XML; Trim() would cut only line one
        var python = "\n            total = 0\n            for x in _context['items']:\n                total += x\n            _context['total'] = ${total}\n        ";
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script8", "script1", "python", python);

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — and ${...} is left alone: it is C#-only shorthand
        var scriptTask = workflow.Activities.OfType<ScriptTask>().Single();
        Assert.AreEqual("python", scriptTask.ScriptFormat);
        Assert.AreEqual("total = 0\nfor x in _context['items']:\n    total += x\n_context['total'] = ${total}", scriptTask.Script);
    }

    [TestMethod]
    [DataRow("x = 1", "x = 1")]
    [DataRow("\r\n  a = 1\r\n  if a:\r\n    b = 2\r\n", "a = 1\nif a:\n  b = 2")]
    [DataRow("  a = 1\n\n  b = 2", "a = 1\n\nb = 2")]
    [DataRow("    a = 1\n  b = 2", "  a = 1\nb = 2")]
    [DataRow(" \n\t \n", "")]
    [DataRow("  s = '''a   \n  b'''", "s = '''a   \nb'''")]
    public void Dedent_RemovesCommonIndentationAndBlankEdges(string input, string expected)
    {
        Assert.AreEqual(expected, Infrastructure.Bpmn.BpmnConverter.Dedent(input));
    }
}
