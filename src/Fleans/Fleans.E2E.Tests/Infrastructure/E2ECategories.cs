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

    /// <summary>
    /// Authentication specs (#771). They need the AppHost booted with <c>FLEANS_E2E_AUTH=true</c>
    /// (Keycloak + JWT bearer on Api + OIDC on Web). Excluded from the default <c>e2e</c> job
    /// (<c>TestCategory!=E2E-Auth</c>) and run by the dedicated <c>e2e-auth</c> job. When the
    /// fixture is not in auth mode these specs report Inconclusive rather than failing.
    /// </summary>
    public const string Auth = "E2E-Auth";
}
