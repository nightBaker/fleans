namespace Fleans.ServiceDefaults.Tests;

/// <summary>
/// Verifies the embedded-resource layout for the vendored Orleans Postgres-reminder
/// schema scripts (#669). The schema-init in <c>EnsureDatabaseSchemaAsync</c> loads
/// these by manifest name — a missing or renamed resource would only blow up at
/// silo startup against a Postgres-reminders deployment. These tests catch the
/// drift at build time instead.
/// </summary>
[TestClass]
public class EmbeddedSchemaResourceTests
{
    [DataTestMethod]
    [DataRow("Fleans.ServiceDefaults.Resources.PostgreSQL_Main.sql")]
    [DataRow("Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql")]
    public void EmbeddedResource_IsPresent(string resourceName)
    {
        var asm = typeof(FleansPersistenceExtensions).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded SQL resource '{resourceName}' missing — check csproj <EmbeddedResource> entries.");
        Assert.IsGreaterThan(0, stream.Length, $"Embedded SQL resource '{resourceName}' is empty.");
    }

    [DataTestMethod]
    [DataRow("Fleans.ServiceDefaults.Resources.PostgreSQL_Main.sql")]
    [DataRow("Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql")]
    public async Task EmbeddedResource_ContainsVersionPin(string resourceName)
    {
        // Drift guard. A silent SDK bump that forgets to re-vendor would skip
        // the version-pin comment we add in the Apache-2.0 attribution header.
        var asm = typeof(FleansPersistenceExtensions).Assembly;
        await using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        StringAssert.Contains(content, "Microsoft.Orleans.Reminders.AdoNet v10.4.0",
            $"{resourceName} is missing the version-pin comment — re-vendor against the matching SDK version (see LICENSE-orleans-vendor).");
    }

    [TestMethod]
    public async Task RemindersScript_IndexesRangeReads()
    {
        // #886: Orleans' ReadRangeRows queries filter on (ServiceId, GrainHash); without this
        // index every 5-minute range re-read scans all of the service's reminders.
        var asm = typeof(FleansPersistenceExtensions).Assembly;
        await using var stream = asm.GetManifestResourceStream("Fleans.ServiceDefaults.Resources.PostgreSQL_Reminders.sql")!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        StringAssert.Contains(content,
            "CREATE INDEX IX_RemindersTable_ServiceId_GrainHash ON OrleansRemindersTable (ServiceId, GrainHash);");
    }
}
