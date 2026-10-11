using System.Dynamic;
using Fleans.Persistence.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Tests;

/// <summary>
/// Security tests for the TypeNameHandling binder used by EfCoreEventStore (and
/// the parallel binder in FleanModelConfiguration). The contract: only types
/// from Fleans.Domain and the curated BCL allowlist may flow through
/// $type-discriminated deserialization. Known Newtonsoft gadget types — even
/// though they ship in BCL assemblies whose names "start with System" — MUST
/// be rejected.
/// </summary>
[TestClass]
public class SerializationBinderSecurityTests
{
    private static readonly JsonSerializerSettings JsonSettings = EfCoreEventStore.JsonSettings;

    [TestMethod]
    public void Binder_RejectsProcessGadget()
    {
        // System.Diagnostics.Process lives in the System.Diagnostics.Process assembly,
        // which is NOT in the allowlist. The previous "name.StartsWith('System')" check
        // accepted it; the explicit allowlist must reject it.
        var malicious = """
            {
                "$type": "System.Diagnostics.Process, System.Diagnostics.Process"
            }
            """;

        var ex = Assert.ThrowsExactly<JsonSerializationException>(
            () => JsonConvert.DeserializeObject<object>(malicious, JsonSettings));

        // Newtonsoft wraps our binder's JsonSerializationException in its own
        // "Error resolving type specified in JSON …" outer exception. Walk the
        // chain to find the binder's message.
        var rejection = FindRejectionMessage(ex);
        Assert.IsNotNull(rejection,
            $"Expected the binder's rejection message in the exception chain, got: {ex}");
        Assert.IsTrue(rejection!.Contains("System.Diagnostics.Process"),
            $"Expected the rejected type name in the rejection, got: {rejection}");
    }

    private static string? FindRejectionMessage(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex.Message.Contains("not allowed"))
                return ex.Message;
            ex = ex.InnerException;
        }
        return null;
    }

    [TestMethod]
    public void Binder_RejectsProcessStartInfoGadget()
    {
        // ProcessStartInfo is the other half of the Process-startup gadget chain.
        var malicious = """
            {
                "$type": "System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process"
            }
            """;

        Assert.ThrowsExactly<JsonSerializationException>(
            () => JsonConvert.DeserializeObject<object>(malicious, JsonSettings));
    }

    [TestMethod]
    public void Binder_RejectsRegistryKeyGadget()
    {
        // RegistryKey lives in Microsoft.Win32.Registry — neither in the allowlist
        // nor in the Fleans.Domain assembly.
        var malicious = """
            {
                "$type": "Microsoft.Win32.RegistryKey, Microsoft.Win32.Registry"
            }
            """;

        Assert.ThrowsExactly<JsonSerializationException>(
            () => JsonConvert.DeserializeObject<object>(malicious, JsonSettings));
    }

    [TestMethod]
    public void Binder_AllowsListOfString()
    {
        // List<string> from System.Private.CoreLib must round-trip — it appears in
        // WorkflowInstanceState (e.g. CandidateUsers, CandidateGroups on user tasks).
        var original = new List<string> { "a", "b", "c" };
        var json = JsonConvert.SerializeObject(original, typeof(object), JsonSettings);

        var deserialized = (List<string>?)JsonConvert.DeserializeObject<object>(json, JsonSettings);

        Assert.IsNotNull(deserialized);
        CollectionAssert.AreEqual(original, deserialized);
    }

    [TestMethod]
    public void Binder_AllowsDictionaryOfStringObject()
    {
        // Dictionary<string, object> from System.Private.CoreLib — workflow variables
        // serialised via the ExpandoObject surrogate end up here.
        var original = new Dictionary<string, object> { ["k"] = "v" };
        var json = JsonConvert.SerializeObject(original, typeof(object), JsonSettings);

        var deserialized = (Dictionary<string, object>?)JsonConvert.DeserializeObject<object>(json, JsonSettings);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("v", deserialized!["k"]);
    }

    [TestMethod]
    public void Binder_AllowsExpandoObject()
    {
        // ExpandoObject lives in System.Linq.Expressions — workflow variables are
        // ExpandoObject across the engine, so a regression here would corrupt
        // event-sourced state for every running workflow.
        dynamic original = new ExpandoObject();
        original.k = "v";

        var json = JsonConvert.SerializeObject((object)original, typeof(object), JsonSettings);
        var deserialized = JsonConvert.DeserializeObject<object>(json, JsonSettings) as ExpandoObject;

        Assert.IsNotNull(deserialized);
        var asDict = (IDictionary<string, object?>)deserialized!;
        Assert.AreEqual("v", asDict["k"]);
    }
}
