using System.Dynamic;
using Fleans.Domain.Errors;
using Fleans.Worker.CustomTasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Plugins.RestCaller.Tests;

[TestClass]
public class RestCallerHandlerTests
{
    private static RestCallerHandler MakeHandler(HttpMessageHandler messageHandler) =>
        new(new HttpClient(messageHandler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<RestCallerHandler>.Instance,
            Substitute.For<IGrainFactory>());

    private static CustomTaskExecutionContext Ctx(Guid? activityInstance = null) =>
        new(WorkflowInstanceId: Guid.NewGuid(),
            WorkflowId: "wf",
            ProcessDefinitionId: null,
            ActivityInstanceId: activityInstance ?? Guid.NewGuid(),
            ActivityId: "ct1",
            TaskType: "rest-call");

    private static IDictionary<string, object?> Inputs(string url, string method = "GET",
        IDictionary<string, object?>? headers = null, string? body = null,
        IEnumerable<int>? successCodes = null, int timeoutSec = 10,
        string? idempotencyKeyHeader = null) => new Dictionary<string, object?>
        {
            ["url"] = url,
            ["method"] = method,
            ["headers"] = headers,
            ["body"] = body,
            ["successCodes"] = successCodes?.Cast<object>().ToList(),
            ["timeoutSec"] = timeoutSec,
            ["idempotencyKeyHeader"] = idempotencyKeyHeader,
        };

    // --- Happy path ---

    [TestMethod]
    public async Task Get_HappyPath_PopulatesResponse()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"hello":"world"}""");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var result = await handler.ExecuteForTest(Inputs($"{mock.BaseUrl}/api"), new ExpandoObject(), Ctx());

        var response = (IDictionary<string, object?>)result["__response"]!;
        Assert.AreEqual(200, response["statusCode"]);
        Assert.AreEqual(true, response["ok"]);
        var body = (IDictionary<string, object?>)response["body"]!;
        Assert.AreEqual("world", body["hello"]);
    }

    [TestMethod]
    public async Task Post_BodyAndHeaders_RoundTripToServer()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 201;
            await ctx.Response.WriteAsync("created");
        });

        var headers = new Dictionary<string, object?>
        {
            ["X-Foo"] = "bar",
            ["X-Empty"] = null,                 // null skipped
            ["X-Number"] = 42,                  // int coerced
        };

        var handler = MakeHandler(new HttpClientHandler());
        await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/things", method: "POST", headers: headers, body: """{"a":1}"""),
            new ExpandoObject(), Ctx());

        var rec = mock.Requests.Single();
        Assert.AreEqual("POST", rec.Method);
        Assert.AreEqual("/things", rec.Path);
        Assert.AreEqual("bar", rec.Headers["X-Foo"]);
        Assert.AreEqual("42", rec.Headers["X-Number"]);
        Assert.IsFalse(rec.Headers.ContainsKey("X-Empty"));
        Assert.AreEqual("application/json; charset=utf-8", rec.Headers["Content-Type"]);
        Assert.AreEqual("""{"a":1}""", rec.Body);
    }

    [TestMethod]
    public async Task Body_WithoutContentTypeHeader_DefaultsToJson()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("");
        });

        var handler = MakeHandler(new HttpClientHandler());
        await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/api", method: "POST", body: """{"x":1}"""),
            new ExpandoObject(), Ctx());

        Assert.AreEqual("application/json; charset=utf-8", mock.Requests.Single().Headers["Content-Type"]);
    }

    [TestMethod]
    public async Task Body_WithUserContentType_UserHeaderWins()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("");
        });

        var headers = new Dictionary<string, object?> { ["Content-Type"] = "text/plain" };
        var handler = MakeHandler(new HttpClientHandler());
        await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/api", method: "POST", headers: headers, body: "raw"),
            new ExpandoObject(), Ctx());

        Assert.IsTrue(mock.Requests.Single().Headers["Content-Type"].StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task IdempotencyKeyHeader_SendsActivityInstanceId()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("");
        });

        var activityInstanceId = Guid.NewGuid();
        var handler = MakeHandler(new HttpClientHandler());
        await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/api", idempotencyKeyHeader: "X-Request-Id"),
            new ExpandoObject(), Ctx(activityInstanceId));

        Assert.AreEqual(activityInstanceId.ToString(), mock.Requests.Single().Headers["X-Request-Id"]);
    }

    [TestMethod]
    public async Task IdempotencyKey_OverridesUserSuppliedHeader()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("");
        });

        var activityInstanceId = Guid.NewGuid();
        var headers = new Dictionary<string, object?> { ["X-Request-Id"] = "user-supplied" };
        var handler = MakeHandler(new HttpClientHandler());
        await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/api", headers: headers, idempotencyKeyHeader: "X-Request-Id"),
            new ExpandoObject(), Ctx(activityInstanceId));

        Assert.AreEqual(activityInstanceId.ToString(), mock.Requests.Single().Headers["X-Request-Id"]);
    }

    // --- successCodes ---

    [TestMethod]
    public async Task SuccessCodes_404Listed_ActivityCompletes()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var result = await handler.ExecuteForTest(
            Inputs($"{mock.BaseUrl}/x", successCodes: new[] { 200, 404 }),
            new ExpandoObject(), Ctx());

        var response = (IDictionary<string, object?>)result["__response"]!;
        Assert.AreEqual(404, response["statusCode"]);
        Assert.AreEqual(false, response["ok"]);
    }

    [TestMethod]
    public async Task SuccessCodes_DefaultExcludes404_ActivityFails()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(Inputs($"{mock.BaseUrl}/x"), new ExpandoObject(), Ctx()));
        Assert.AreEqual("404", ex.GetActivityErrorState().Code);
    }

    // --- failure codes ---

    [TestMethod]
    public async Task NetworkError_FailsWith502()
    {
        var handler = MakeHandler(new ThrowingHandler(new HttpRequestException("connection refused")));
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(Inputs("http://nonexistent.invalid/"), new ExpandoObject(), Ctx()));
        Assert.AreEqual("502", ex.GetActivityErrorState().Code);
    }

    [TestMethod]
    public async Task Timeout_FailsWith504()
    {
        var handler = MakeHandler(new SlowHandler(TimeSpan.FromSeconds(5)));
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(
                Inputs("http://example.com/", timeoutSec: 1),
                new ExpandoObject(), Ctx()));
        Assert.AreEqual("504", ex.GetActivityErrorState().Code);
    }

    [TestMethod]
    public async Task BadUrl_FailsWith400()
    {
        var handler = MakeHandler(new HttpClientHandler());
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(Inputs("not a url"), new ExpandoObject(), Ctx()));
        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    [TestMethod]
    public async Task UnsupportedMethod_FailsWith400()
    {
        var handler = MakeHandler(new HttpClientHandler());
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(Inputs("http://example.com/", method: "TRACE"), new ExpandoObject(), Ctx()));
        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    [TestMethod]
    public async Task TimeoutOutOfRange_FailsWith400()
    {
        var handler = MakeHandler(new HttpClientHandler());
        var ex = await Assert.ThrowsExactlyAsync<CustomTaskFailedActivityException>(() =>
            handler.ExecuteForTest(Inputs("http://example.com/", timeoutSec: 0), new ExpandoObject(), Ctx()));
        Assert.AreEqual("400", ex.GetActivityErrorState().Code);
    }

    // --- JSON content-type detection ---

    [TestMethod]
    public async Task ResponseContentType_VendorPlusJson_DeserializesAsJson()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/vnd.api+json";
            await ctx.Response.WriteAsync("""{"k":1}""");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var result = await handler.ExecuteForTest(Inputs($"{mock.BaseUrl}/x"), new ExpandoObject(), Ctx());
        var response = (IDictionary<string, object?>)result["__response"]!;
        var body = (IDictionary<string, object?>)response["body"]!;
        Assert.AreEqual(1L, body["k"]);
    }

    [TestMethod]
    public async Task ResponseContentType_NotJson_KeepsBodyAsString()
    {
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-json-trace";   // looks JSON-ish but isn't application/json or +json
            await ctx.Response.WriteAsync("""{"k":1}""");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var result = await handler.ExecuteForTest(Inputs($"{mock.BaseUrl}/x"), new ExpandoObject(), Ctx());
        var response = (IDictionary<string, object?>)result["__response"]!;
        Assert.AreEqual("""{"k":1}""", response["body"]);
    }

    // --- security ---

    [TestMethod]
    public async Task ResponseBody_WithMaliciousTypeHandle_DoesNotInstantiate()
    {
        // If TypeNameHandling were enabled, this $type field would attempt to construct a FileInfo —
        // a real CVE class. The default (and our explicit setting) is None: $type is a plain string field.
        await using var mock = await MockServer.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"$type":"System.IO.FileInfo, System","Path":"/etc/passwd"}""");
        });

        var handler = MakeHandler(new HttpClientHandler());
        var result = await handler.ExecuteForTest(Inputs($"{mock.BaseUrl}/x"), new ExpandoObject(), Ctx());
        var response = (IDictionary<string, object?>)result["__response"]!;
        var body = (IDictionary<string, object?>)response["body"]!;
        Assert.AreEqual("System.IO.FileInfo, System", body["$type"]);   // plain string, no type instantiation
        Assert.AreEqual("/etc/passwd", body["Path"]);
    }

    // --- helpers ---

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw ex;
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

/// <summary>
/// Test seam exposing the protected <c>ExecuteAsync</c> for direct invocation.
/// Mirrors the pattern other plugin-author tests will use.
/// </summary>
internal static class RestCallerHandlerTestExtensions
{
    public static Task<IDictionary<string, object?>> ExecuteForTest(
        this RestCallerHandler handler,
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken ct = default)
        => RestCallerHandlerTestSeam.Invoke(handler, resolvedInputs, variables, context, ct);
}

/// <summary>
/// Reflection-free test seam — re-declared as a sibling to access the protected ExecuteAsync.
/// (Kept inside this assembly so we don't expose a test-only API on the production handler.)
/// </summary>
file static class RestCallerHandlerTestSeam
{
    public static Task<IDictionary<string, object?>> Invoke(
        RestCallerHandler handler, IDictionary<string, object?> inputs, ExpandoObject variables,
        CustomTaskExecutionContext context, CancellationToken ct)
    {
        // Use reflection: protected method, can't reach across assembly boundaries cleanly.
        var method = typeof(RestCallerHandler).GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RestCallerHandler.ExecuteAsync not found");
        return (Task<IDictionary<string, object?>>)method.Invoke(handler, new object[] { inputs, variables, context, ct })!;
    }
}
