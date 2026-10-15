using Fleans.Application;
using Fleans.Application.QueryModels;
using Fleans.Mcp.Tools;
using NSubstitute;

namespace Fleans.Mcp.Tests;

[TestClass]
public class InstanceToolsTests
{
    [TestMethod]
    public async Task ListInstances_PassesBusinessIdThroughAsAnExactMatchFilter()
    {
        // Arrange
        var queryService = Substitute.For<IWorkflowQueryService>();
        queryService.GetInstancesByKey(default!, default!, default)
            .ReturnsForAnyArgs(new PagedResult<WorkflowInstanceInfo>([], 0, 1, 50));

        // Act — `,` and `|` would split a Sieve filters term
        await InstanceTools.ListInstances(queryService, "order-process", businessId: "ACME, Inc|42");

        // Assert
        await queryService.Received(1).GetInstancesByKey("order-process", Arg.Any<PageRequest>(), "ACME, Inc|42");
    }
}
