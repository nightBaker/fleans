namespace Fleans.Web.Services;

/// <summary>
/// Which environment this Admin UI's cluster is, and where its versions can be promoted (#900),
/// bound from <c>Fleans:Environment</c>. Promotion is hop by hop: dev lists <c>test</c>, test lists
/// <c>prod</c>; each target applies its own <c>Fleans:Deployments:RequiredApprovals</c>.
/// </summary>
public sealed class EnvironmentOptions
{
    public const string SectionName = "Fleans:Environment";

    /// <summary>This cluster's name in the deploy dialog ("Deploy · dev"). Empty: "this Fleans cluster".</summary>
    public string? Name { get; set; }

    public List<PromotionTarget> PromotionTargets { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "this Fleans cluster" : Name;
}

/// <summary>
/// Another Fleans cluster, reached through its REST API (<c>POST /Deployments/requests</c>).
/// <see cref="TokenUrl"/>, <see cref="ClientId"/> and <see cref="ClientSecret"/> configure an
/// OAuth2 client-credentials token for a target with authentication on; leave them empty for a
/// target without.
/// </summary>
public sealed class PromotionTarget
{
    public string Name { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scope { get; set; }

    public bool UsesClientCredentials => !string.IsNullOrWhiteSpace(ClientId);
}
