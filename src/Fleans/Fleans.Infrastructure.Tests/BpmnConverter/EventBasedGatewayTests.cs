using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class EventBasedGatewayTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseEventBasedGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithEventBasedGateway("ebg-workflow", "ebg1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<EventBasedGateway>().FirstOrDefault(g => g.ActivityId == "ebg1");
        Assert.IsNotNull(gateway, "EventBasedGateway should be parsed");
    }
}
