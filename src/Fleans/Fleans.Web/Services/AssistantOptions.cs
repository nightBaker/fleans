namespace Fleans.Web.Services;

/// <summary>
/// The editor's AI assistant backend (#1015), bound from <c>Fleans:Assistant</c>. Fleans ships the
/// panel and the client, not a backend: <see cref="Endpoint"/> points at any service implementing
/// the <c>Fleans.Assistant.Contracts</c> HTTP contract. Without it the panel is hidden.
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Fleans:Assistant";

    /// <summary>The backend's base URL; the client runs the AG-UI agent at <c>{Endpoint}/assistant/agent</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>How long one response, including its whole event stream, may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint);
}
