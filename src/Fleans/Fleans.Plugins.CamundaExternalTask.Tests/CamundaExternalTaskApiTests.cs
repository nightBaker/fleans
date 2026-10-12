using System.Dynamic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleans.Application.Grains;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

/// <summary>
/// The REST surface as a Camunda 7 external-task client sees it: request/response JSON shapes,
/// status codes and error bodies. The engine is replaced by a substitute callback grain.
/// </summary>
[TestClass]
public class CamundaExternalTaskApiTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private SqliteConnection? _keepAlive;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ManualClock _clock = null!;
    private IWorkflowInstanceCallback _callback = null!;
    private ExpandoObject? _processVariables;

    /// <summary>The external-task table provider; SQLite here, PostgreSQL in the derived class.</summary>
    protected virtual Task<Action<DbContextOptionsBuilder>> CreateDatabase()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        return Task.FromResult<Action<DbContextOptionsBuilder>>(o => o.UseSqlite(connectionString));
    }

    [TestInitialize]
    public async Task Setup()
    {
        var configureDb = await CreateDatabase();

        _clock = new ManualClock(T0);
        _processVariables = Expando(("amount", 42L), ("customer", "bob"), ("vip", true));
        _callback = Substitute.For<IWorkflowInstanceCallback>();
        _callback.GetVariablesIfActive(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(_ => ValueTask.FromResult(_processVariables));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IWorkflowInstanceCallback>(Arg.Any<Guid>(), Arg.Any<string?>()).Returns(_callback);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(grainFactory);
        builder.Services.AddCamundaExternalTasks(configureDb);
        _app = builder.Build();
        _app.MapCamundaExternalTaskApi();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_app is not null) // null when a derived class skipped the test in CreateDatabase
            await _app.DisposeAsync();
        _keepAlive?.Dispose();
    }

    [TestMethod]
    public async Task FetchAndLock_returns_camunda_shaped_tasks_with_typed_variables()
    {
        // Arrange
        var task = await Seed("charge-card", inputs: [new InputMapping("=amount", "toCharge")]);

        // Act — the body camunda-external-task-client sends.
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 10,
            usePriority = true,
            topics = new[] { new { topicName = "charge-card", lockDuration = 10_000, deserializeValues = false } },
        });

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.ToString(),
            "camunda-external-task-client-js parses only an exact 'application/json' content type.");
        var tasks = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        Assert.HasCount(1, tasks);
        var locked = tasks[0]!;
        Assert.AreEqual(task.Id, (string?)locked["id"]);
        Assert.AreEqual("charge-card", (string?)locked["topicName"]);
        Assert.AreEqual("w1", (string?)locked["workerId"]);
        // Seeding advanced the clock by 1 ms; the lock lasts 10 s from the fetch.
        Assert.AreEqual("2026-01-01T12:00:10.001+0000", (string?)locked["lockExpirationTime"]);
        Assert.AreEqual(task.WorkflowInstanceId.ToString("D"), (string?)locked["processInstanceId"]);
        Assert.AreEqual("order-process", (string?)locked["processDefinitionKey"]);
        Assert.AreEqual("ct1", (string?)locked["activityId"]);
        var variables = locked["variables"]!;
        Assert.AreEqual("Integer", (string?)variables["amount"]!["type"]);
        Assert.AreEqual(42, (int)variables["amount"]!["value"]!);
        Assert.AreEqual("String", (string?)variables["customer"]!["type"]);
        Assert.AreEqual("Boolean", (string?)variables["vip"]!["type"]);
        Assert.AreEqual(42, (int)variables["toCharge"]!["value"]!, "Input mappings are exposed as variables.");
    }

    [TestMethod]
    public async Task FetchAndLock_honours_variable_names_and_local_variables()
    {
        // Arrange
        await Seed("charge-card", inputs: [new InputMapping("=customer", "who")]);

        // Act
        var named = await Fetch("w1", "charge-card", variables: ["amount"]);
        await _http.PostAsync($"/engine-rest/external-task/{named[0]!["id"]}/unlock", null);
        var localOnly = await Fetch("w1", "charge-card", localVariables: true);

        // Assert
        CollectionAssert.AreEquivalent(new[] { "amount" }, Names(named[0]!["variables"]!));
        CollectionAssert.AreEquivalent(new[] { "who" }, Names(localOnly[0]!["variables"]!));
    }

    [TestMethod]
    public async Task Locked_task_becomes_fetchable_again_after_its_lock_expires()
    {
        // Arrange
        await Seed("charge-card");
        Assert.HasCount(1, await Fetch("w1", "charge-card", lockDuration: 1_000));

        // Act
        var whileLocked = await Fetch("w2", "charge-card");
        _clock.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = await Fetch("w2", "charge-card");

        // Assert
        Assert.HasCount(0, whileLocked);
        Assert.HasCount(1, afterExpiry);
        Assert.AreEqual("w2", (string?)afterExpiry[0]!["workerId"]);
    }

    [TestMethod]
    public async Task FetchAndLock_drops_tasks_whose_activity_is_no_longer_active()
    {
        // Arrange — the workflow moved on without the worker (e.g. interrupted by a boundary timer).
        await Seed("charge-card");
        _processVariables = null;

        // Act
        var tasks = await Fetch("w1", "charge-card");
        var count = await _http.GetFromJsonAsync<JsonObject>("/engine-rest/external-task/count");

        // Assert
        Assert.HasCount(0, tasks);
        Assert.AreEqual(0, (int)count!["count"]!);
    }

    [TestMethod]
    public async Task Complete_converts_typed_variables_and_applies_output_mappings()
    {
        // Arrange
        var task = await Seed("charge-card", outputs: [new OutputMapping("=receipt.id", "receiptId")]);
        await Fetch("w1", "charge-card");
        ExpandoObject? completed = null;
        await _callback.CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Do<ExpandoObject>(v => completed = v));

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new Dictionary<string, object>
            {
                ["charged"] = new { value = true, type = "Boolean" },
                ["total"] = new { value = 99.5, type = "Double" },
                ["order"] = new { value = "{\"lines\":[1,2]}", type = "Json" },
                ["customerDto"] = new
                {
                    value = "{\"name\":\"bob\"}",
                    type = "Object",
                    valueInfo = new { objectTypeName = "com.acme.Customer", serializationDataFormat = "application/json" },
                },
            },
            localVariables = new Dictionary<string, object>
            {
                ["receipt"] = new { value = "{\"id\":\"R-1\"}", type = "Json" },
            },
        });

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        await _callback.Received(1).CompleteActivity("ct1", task.ActivityInstanceId, Arg.Any<ExpandoObject>());
        var outputs = (IDictionary<string, object?>)completed!;
        Assert.AreEqual(true, outputs["charged"]);
        Assert.AreEqual(99.5, outputs["total"]);
        CollectionAssert.AreEqual(new List<object?> { 1L, 2L }, (List<object?>)((IDictionary<string, object?>)outputs["order"]!)["lines"]!);
        Assert.AreEqual("bob", ((IDictionary<string, object?>)outputs["customerDto"]!)["name"]);
        Assert.AreEqual("R-1", outputs["receiptId"], "Output mappings read local variables.");
        Assert.IsFalse(outputs.ContainsKey("receipt"), "Local variables do not leak into the workflow scope.");
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _http.GetAsync($"/engine-rest/external-task/{task.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task Errors_use_camunda_status_codes_and_bodies()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var missing = await _http.PostAsJsonAsync("/engine-rest/external-task/nope/complete", new { workerId = "w1" });
        var wrongWorker = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w2" });
        var unsupported = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new
        {
            workerId = "w1",
            variables = new { file = new { value = "AAEC", type = "Bytes" } },
        });

        // Assert
        await AssertError(missing, HttpStatusCode.NotFound, "InvalidRequestException");
        await AssertError(wrongWorker, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(unsupported, HttpStatusCode.BadRequest, "InvalidRequestException");
        await _callback.DidNotReceive().CompleteActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task BpmnError_fails_the_activity_with_the_error_code()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/bpmnError",
            new { workerId = "w1", errorCode = "DECLINED", errorMessage = "no funds" });

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        await _callback.Received(1).FailActivity("ct1", task.ActivityInstanceId,
            Arg.Is<Exception>(e => e is ActivityException
                && ((ActivityException)e).GetActivityErrorState().Code == "DECLINED"
                && ((ActivityException)e).GetActivityErrorState().Message == "no funds"));
    }

    [TestMethod]
    public async Task Failure_with_retries_left_waits_for_the_retry_timeout()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card");

        // Act
        var response = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "down", retries = 2, retryTimeout = 5_000 });
        var beforeTimeout = await Fetch("w1", "charge-card");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var afterTimeout = await Fetch("w1", "charge-card");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.HasCount(0, beforeTimeout);
        Assert.HasCount(1, afterTimeout);
        Assert.AreEqual(2, (int)afterTimeout[0]!["retries"]!);
        Assert.AreEqual("down", (string?)afterTimeout[0]!["errorMessage"]);
        await _callback.DidNotReceive().FailActivity(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Exception>());
    }

    [TestMethod]
    public async Task ExtendLock_keeps_the_task_locked_past_its_original_expiry()
    {
        // Arrange
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card", lockDuration: 1_000);

        // Act
        var extended = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w1", newDuration = 60_000 });
        var byOther = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w2", newDuration = 60_000 });
        _clock.Advance(TimeSpan.FromSeconds(2));
        var fetchedByOther = await Fetch("w2", "charge-card");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, extended.StatusCode);
        await AssertError(byOther, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.HasCount(0, fetchedByOther);
    }

    [TestMethod]
    public async Task Query_endpoints_filter_by_topic_and_lock_state()
    {
        // Arrange
        await Seed("charge-card");
        await Seed("ship-order");
        await Fetch("w1", "charge-card");

        // Act
        var locked = await _http.GetFromJsonAsync<JsonArray>("/engine-rest/external-task?locked=true");
        var byTopic = await _http.GetFromJsonAsync<JsonArray>("/engine-rest/external-task?topicName=ship-order");
        var topics = await _http.GetFromJsonAsync<List<string>>("/engine-rest/external-task/topic-names");

        // Assert
        Assert.AreEqual("charge-card", (string?)locked!.Single()!["topicName"]);
        Assert.AreEqual("ship-order", (string?)byTopic!.Single()!["topicName"]);
        CollectionAssert.AreEqual(new[] { "charge-card", "ship-order" }, topics);
    }

    [TestMethod]
    public async Task Late_report_from_a_worker_whose_lock_was_handed_over_is_rejected()
    {
        // Arrange — w1's lock expires and w2 takes the task over.
        var task = await Seed("charge-card");
        await Fetch("w1", "charge-card", lockDuration: 1_000);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.HasCount(1, await Fetch("w2", "charge-card"));

        // Act — w1 reports late.
        var lateFailure = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/failure",
            new { workerId = "w1", errorMessage = "late", retries = 3, retryTimeout = 0 });
        var lateComplete = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w1" });
        var lateExtend = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/extendLock",
            new { workerId = "w1", newDuration = 60_000 });
        var ownerComplete = await _http.PostAsJsonAsync($"/engine-rest/external-task/{task.Id}/complete", new { workerId = "w2" });

        // Assert — w2 keeps its lock and its result is the one the workflow gets.
        await AssertError(lateFailure, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(lateComplete, HttpStatusCode.InternalServerError, "BadUserRequestException");
        await AssertError(lateExtend, HttpStatusCode.BadRequest, "InvalidRequestException");
        Assert.AreEqual(HttpStatusCode.NoContent, ownerComplete.StatusCode);
        await _callback.Received(1).CompleteActivity("ct1", task.ActivityInstanceId, Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task A_task_whose_variables_cannot_be_read_is_failed_without_blocking_the_others()
    {
        // Arrange — the first task's variable scope is unknown to the engine (deterministic error).
        var poison = await Seed("charge-card");
        var healthy = await Seed("charge-card");
        _callback.GetVariablesIfActive(poison.ActivityInstanceId, Arg.Any<Guid>())
            .Returns<ValueTask<ExpandoObject?>>(_ => throw new InvalidOperationException("unknown scope"));

        // Act
        var tasks = await Fetch("w1", "charge-card");

        // Assert
        Assert.AreEqual(healthy.Id, (string?)tasks.Single()!["id"]);
        await _callback.Received(1).FailActivity("ct1", poison.ActivityInstanceId, Arg.Any<InvalidOperationException>());
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _http.GetAsync($"/engine-rest/external-task/{poison.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task FetchAndLock_rejects_a_lock_duration_out_of_range()
    {
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId = "w1",
            maxTasks = 1,
            topics = new[] { new { topicName = "charge-card", lockDuration = long.MaxValue } },
        });

        await AssertError(response, HttpStatusCode.BadRequest, "InvalidRequestException");
    }

    [TestMethod]
    public async Task Schema_initializers_of_several_replicas_can_start_at_once()
    {
        var factory = _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>();

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => new ExternalTaskSchemaInitializer(factory).StartAsync(CancellationToken.None)));

        await Seed("charge-card");
        Assert.HasCount(1, await Fetch("w1", "charge-card"));
    }

    [TestMethod]
    public async Task Schema_initializer_can_run_again_on_an_existing_table()
    {
        // Arrange — the host already created the table at startup; a second replica starts.
        await Seed("charge-card");
        var initializer = new ExternalTaskSchemaInitializer(
            _app.Services.GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>());

        // Act
        await initializer.StartAsync(CancellationToken.None);

        // Assert — existing rows survive.
        Assert.HasCount(1, await Fetch("w1", "charge-card"));
    }

    private async Task<ExternalTaskEntity> Seed(
        string topic, List<InputMapping>? inputs = null, List<OutputMapping>? outputs = null)
    {
        var activityInstanceId = Guid.NewGuid();
        var entity = new ExternalTaskEntity
        {
            Id = activityInstanceId.ToString("D"),
            TopicName = topic,
            WorkflowInstanceId = Guid.NewGuid(),
            ProcessDefinitionKey = "order-process",
            ProcessDefinitionId = "order-process:1",
            ActivityId = "ct1",
            ActivityInstanceId = activityInstanceId,
            VariablesId = Guid.NewGuid(),
            InputMappingsJson = JsonSerializer.Serialize(inputs ?? []),
            OutputMappingsJson = JsonSerializer.Serialize(outputs ?? []),
            CreateTime = _clock.GetUtcNow().UtcDateTime,
        };
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await using var scope = _app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ExternalTaskDbContext>>().CreateDbContextAsync();
        db.ExternalTasks.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private async Task<JsonArray> Fetch(
        string workerId, string topic, long lockDuration = 10_000, string[]? variables = null, bool localVariables = false)
    {
        var response = await _http.PostAsJsonAsync("/engine-rest/external-task/fetchAndLock", new
        {
            workerId,
            maxTasks = 10,
            topics = new[] { new { topicName = topic, lockDuration, variables, localVariables } },
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.AreEqual(status, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.ToString());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual(type, (string?)body["type"]);
        Assert.IsFalse(string.IsNullOrEmpty((string?)body["message"]));
    }

    private static string[] Names(JsonNode variables) => variables.AsObject().Select(kv => kv.Key).ToArray();

    private static ExpandoObject Expando(params (string Name, object? Value)[] values)
    {
        var expando = new ExpandoObject();
        foreach (var (name, value) in values)
            ((IDictionary<string, object?>)expando)[name] = value;
        return expando;
    }

    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
