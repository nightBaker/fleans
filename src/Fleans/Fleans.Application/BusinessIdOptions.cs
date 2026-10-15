namespace Fleans.Application;

/// <summary>
/// Business id settings (#824), bound from the <c>Fleans:BusinessId</c> configuration section.
/// Every core silo must use the same value.
/// </summary>
public sealed class BusinessIdOptions
{
    public const string SectionName = "Fleans:BusinessId";

    /// <summary>
    /// When true, a start that supplies a business id is rejected (HTTP 409) while another
    /// started, non-terminal root instance of the same process definition key (any version)
    /// carries it. Off by default, as in Camunda 8.
    /// </summary>
    public bool EnforceUniqueness { get; set; }
}
