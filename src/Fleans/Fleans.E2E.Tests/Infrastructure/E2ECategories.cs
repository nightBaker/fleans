namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Test categories layered on top of <c>[TestCategory("E2E")]</c>.
/// </summary>
public static class E2ECategories
{
    /// <summary>
    /// Representative subset (basic flow, user task, call activity, compensation, timer,
    /// message, signal) re-run by CI against non-default providers — Postgres persistence
    /// and Kafka / Azure Queue streaming (see the <c>e2e-providers</c> job in dotnet.yml).
    /// Keep it small: every spec here runs once per provider leg.
    /// </summary>
    public const string Smoke = "E2E-Smoke";
}
