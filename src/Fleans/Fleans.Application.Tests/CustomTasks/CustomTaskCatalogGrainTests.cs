using Fleans.Application.CustomTasks;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskCatalogGrainTests : WorkflowTestBase
{
    private ICustomTaskCatalogGrain GetCatalog() =>
        Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0);

    [TestMethod]
    public async Task Register_NewEntry_AppearsInGetAll()
    {
        var catalog = GetCatalog();

        await catalog.Register(new CustomTaskRegistration("rest-call", "REST Caller", null, "worker-A"));

        var entries = await catalog.GetAll();
        var rest = entries.SingleOrDefault(e => e.TaskType == "rest-call");
        Assert.IsNotNull(rest);
        Assert.AreEqual("REST Caller", rest!.DisplayName);
        Assert.HasCount(1, rest.SiloNames);
        Assert.AreEqual("worker-A", rest.SiloNames[0]);
    }

    [TestMethod]
    public async Task Register_SameTaskTypeAndSilo_IsIdempotent()
    {
        var catalog = GetCatalog();

        await catalog.Register(new CustomTaskRegistration("rest-call", null, null, "worker-A"));
        await catalog.Register(new CustomTaskRegistration("rest-call", "Updated Display", null, "worker-A"));

        var entries = await catalog.GetAll();
        var rest = entries.Single(e => e.TaskType == "rest-call");
        Assert.HasCount(1, rest.SiloNames);
        Assert.AreEqual("Updated Display", rest.DisplayName);
    }

    [TestMethod]
    public async Task Register_SameTaskTypeDifferentSilos_AggregatesIntoOneEntry()
    {
        var catalog = GetCatalog();

        await catalog.Register(new CustomTaskRegistration("rest-call", null, null, "worker-1"));
        await catalog.Register(new CustomTaskRegistration("rest-call", null, null, "worker-2"));

        var entries = await catalog.GetAll();
        var rest = entries.Single(e => e.TaskType == "rest-call");
        Assert.HasCount(2, rest.SiloNames);
        CollectionAssert.AreEquivalent(new[] { "worker-1", "worker-2" }, rest.SiloNames.ToList());
    }

    [TestMethod]
    public async Task Get_UnknownTaskType_ReturnsNull()
    {
        var catalog = GetCatalog();

        var result = await catalog.Get("never-registered");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Get_KnownTaskType_ReturnsAggregatedEntry()
    {
        var catalog = GetCatalog();
        var schema = new CustomTaskParameterSchema(new[]
        {
            new CustomTaskParameterSpec("to", "Recipient", CustomTaskParameterType.String,
                Required: true, Description: "Email address", DefaultValue: null),
            new CustomTaskParameterSpec("body", "Body", CustomTaskParameterType.MultilineString,
                Required: false, Description: null, DefaultValue: ""),
        });

        await catalog.Register(new CustomTaskRegistration("email-send", "Email Sender", schema, "worker-X"));

        var entry = await catalog.Get("email-send");

        Assert.IsNotNull(entry);
        Assert.AreEqual("email-send", entry!.TaskType);
        Assert.AreEqual("Email Sender", entry.DisplayName);
        Assert.IsNotNull(entry.ParameterSchema);
        Assert.HasCount(2, entry.ParameterSchema!.Parameters);
        Assert.AreEqual("to", entry.ParameterSchema.Parameters[0].Name);
        Assert.AreEqual(CustomTaskParameterType.String, entry.ParameterSchema.Parameters[0].Type);
        Assert.IsTrue(entry.ParameterSchema.Parameters[0].Required);
        Assert.AreEqual(CustomTaskParameterType.MultilineString, entry.ParameterSchema.Parameters[1].Type);
        Assert.AreEqual("worker-X", entry.SiloNames.Single());
    }

    [TestMethod]
    public async Task Get_PluginWithoutSchema_ReturnsNullParameterSchema()
    {
        var catalog = GetCatalog();

        await catalog.Register(new CustomTaskRegistration("opaque-task", null, null, "worker-Y"));

        var entry = await catalog.Get("opaque-task");

        Assert.IsNotNull(entry);
        Assert.IsNull(entry!.ParameterSchema);
    }

    [TestMethod]
    public async Task Get_PluginWithMapAndListParameters_RoundTripsItemType()
    {
        var catalog = GetCatalog();
        var schema = new CustomTaskParameterSchema(new[]
        {
            new CustomTaskParameterSpec("url", "URL", CustomTaskParameterType.String,
                Required: true, Description: null, DefaultValue: null),
            new CustomTaskParameterSpec("headers", "HTTP Headers", CustomTaskParameterType.Map,
                Required: false, Description: "Repeat for each header.", DefaultValue: null,
                ItemType: CustomTaskParameterType.String),
            new CustomTaskParameterSpec("successCodes", "Success Codes", CustomTaskParameterType.List,
                Required: false, Description: "HTTP codes treated as success.", DefaultValue: null,
                ItemType: CustomTaskParameterType.Integer),
        });

        await catalog.Register(new CustomTaskRegistration("rest-call", "REST Caller", schema, "worker-Z"));

        var entry = await catalog.Get("rest-call");

        Assert.IsNotNull(entry);
        Assert.IsNotNull(entry!.ParameterSchema);
        var headers = entry.ParameterSchema!.Parameters.Single(p => p.Name == "headers");
        Assert.AreEqual(CustomTaskParameterType.Map, headers.Type);
        Assert.AreEqual(CustomTaskParameterType.String, headers.ItemType);
        var codes = entry.ParameterSchema.Parameters.Single(p => p.Name == "successCodes");
        Assert.AreEqual(CustomTaskParameterType.List, codes.Type);
        Assert.AreEqual(CustomTaskParameterType.Integer, codes.ItemType);
    }

    [TestMethod]
    public async Task Get_IsCaseInsensitiveOnTaskType()
    {
        var catalog = GetCatalog();

        await catalog.Register(new CustomTaskRegistration("rest-call", null, null, "worker-A"));

        var lower = await catalog.Get("rest-call");
        var upper = await catalog.Get("REST-CALL");

        Assert.IsNotNull(lower);
        Assert.IsNotNull(upper);
        Assert.AreEqual(lower!.TaskType, upper!.TaskType);
    }
}
