using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace Fleans.Api.Tests;

[TestClass]
public class GlobalExceptionHandlerTests
{
    [TestMethod]
    public async Task Gateway_shedding_load_answers_503()
    {
        // Arrange — a silo gateway with Fleans:LoadShedding on rejects client calls when CPU is high.
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        // Act
        var handled = await handler.TryHandleAsync(context, new GatewayTooBusyException(), CancellationToken.None);

        // Assert
        Assert.IsTrue(handled);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
