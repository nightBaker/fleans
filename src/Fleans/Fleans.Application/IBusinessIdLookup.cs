namespace Fleans.Application;

/// <summary>
/// Uniqueness lookup for <see cref="Grains.IBusinessIdGuardGrain"/> (#824). Implementations must
/// read the primary (command) store, never a read replica: the guard relies on seeing the
/// instance the previous serialized call just wrote.
/// </summary>
public interface IBusinessIdLookup
{
    /// <summary>
    /// Returns the id of a started, non-terminal root instance of
    /// <paramref name="processDefinitionKey"/> (any version) carrying
    /// <paramref name="businessId"/>, or <c>null</c>.
    /// </summary>
    Task<Guid?> FindActiveRootInstance(string processDefinitionKey, string businessId);
}
