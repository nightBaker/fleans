using System.Net;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/69-api-read-endpoints-negative-paths/test-plan.md (Instances section)
// The happy path of GET /Instances/{id}/state is exercised by every other spec; this class
// pins the not-found contract.
[TestClass]
[TestCategory("E2E")]
public class InstancesApiTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task GetState_UnknownInstance_Returns404WithMessage()
    {
        var unknown = Guid.NewGuid();
        using var response = await ApiClient.GetStateRawAsync(unknown);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, unknown.ToString());
        StringAssert.Contains(body, "not found");
    }

    [TestMethod]
    public async Task GetState_NonGuidId_Returns404()
    {
        // The route constraint is {instanceId:guid}; a non-GUID segment matches no route.
        using var response = await AspireFixture.ApiHttpClient.GetAsync("/Instances/not-a-guid/state");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
