using System.Dynamic;
using System.Net;
using System.Net.Http.Json;
using Fleans.Api.Admission;
using Fleans.Api.Controllers;
using Fleans.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Fleans.Api.Tests;

/// <summary>
/// The admission limit through the real HTTP pipeline and <see cref="ExecutionController"/>, wired
/// as <c>Program.cs</c> wires it (<see cref="AdmissionExtensions"/>).
/// </summary>
[TestClass]
public class AdmissionApiTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IWorkflowCommandService _commands = null!;
    private TaskCompletionSource<Guid> _startGate = null!;
    private int _startsReceived;

    [TestInitialize]
    public async Task Setup()
    {
        _startGate = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands = Substitute.For<IWorkflowCommandService>();
        _commands.StartWorkflow(Arg.Any<string>(), Arg.Any<ExpandoObject?>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _startsReceived);
                return _startGate.Task;
            });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleans:Admission:InitialLimit"] = "2",
            ["Fleans:Admission:MinLimit"] = "2",
            ["Fleans:Admission:MaxLimit"] = "2",
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(ExecutionController).Assembly);
        builder.Services.AddSingleton(_commands);
        builder.Services.AddFleansAdmission(builder.Configuration);

        _app = builder.Build();
        _app.UseFleansAdmission();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _startGate.TrySetResult(Guid.NewGuid());
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Start() =>
        _client.PostAsJsonAsync("/Execution/start", new { WorkflowId = "wf" });

    [TestMethod]
    public async Task ConcurrentStartsOverTheLimit_Get429WithRetryAfter_AndAreAdmittedOnceSlotsFree()
    {
        // Arrange — two starts hold both slots.
        var first = Start();
        var second = Start();
        await WaitUntil(() => Volatile.Read(ref _startsReceived) == 2);

        // Act
        using var rejected = await Start();

        // Assert
        Assert.AreEqual((HttpStatusCode)429, rejected.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter?.Delta);
        Assert.AreEqual(2, Volatile.Read(ref _startsReceived), "a rejected start must not reach the cluster");

        // The held starts complete; a new start is admitted.
        _startGate.SetResult(Guid.NewGuid());
        using (var r1 = await first) Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
        using (var r2 = await second) Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
        using var admitted = await Start();
        Assert.AreEqual(HttpStatusCode.OK, admitted.StatusCode);
    }

    [TestMethod]
    public async Task Disabled_AdmitsEveryStart()
    {
        // Arrange
        await _app.DisposeAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleans:Admission:Enabled"] = "false",
            ["Fleans:Admission:InitialLimit"] = "1",
            ["Fleans:Admission:MinLimit"] = "1",
            ["Fleans:Admission:MaxLimit"] = "1",
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(ExecutionController).Assembly);
        builder.Services.AddSingleton(_commands);
        builder.Services.AddFleansAdmission(builder.Configuration);
        _app = builder.Build();
        _app.UseFleansAdmission();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        // Act
        var starts = Enumerable.Range(0, 3).Select(_ => Start()).ToList();
        await WaitUntil(() => Volatile.Read(ref _startsReceived) == 3);
        _startGate.SetResult(Guid.NewGuid());

        // Assert
        foreach (var start in starts)
        {
            using var response = await start;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the held requests.");
            await Task.Delay(10);
        }
    }
}
