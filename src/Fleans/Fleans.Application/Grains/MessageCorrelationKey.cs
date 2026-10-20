namespace Fleans.Application.Grains;

public static class MessageCorrelationKey
{
    /// <summary>
    /// Longest composite key the correlation tables store (the <c>MessageCorrelations.Key</c>
    /// column). Enforced here so SQLite, which ignores column lengths, rejects the same keys
    /// Postgres would.
    /// </summary>
    public const int MaxLength = 1024;

    public static string Build(string messageName, string correlationKey)
        => TryBuild(messageName, correlationKey) ?? throw new ArgumentException(
            $"The message name plus the escaped correlation key is longer than {MaxLength} characters.");

    /// <summary>
    /// Like <see cref="Build"/>, but returns <c>null</c> when the composite is over
    /// <see cref="MaxLength"/> — no subscription can exist under such a key.
    /// </summary>
    public static string? TryBuild(string messageName, string correlationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        var key = $"{messageName}/{Uri.EscapeDataString(correlationKey)}";
        return key.Length <= MaxLength ? key : null;
    }

    /// <summary>
    /// The message-name part of a key produced by <see cref="Build"/>, for display. Returns the
    /// input unchanged when it has no separator, so one malformed row cannot break a listing.
    /// </summary>
    public static string MessageNameOf(string key)
    {
        var separator = key.LastIndexOf('/');
        return separator < 0 ? key : key[..separator];
    }

    /// <summary>
    /// Splits a key produced by <see cref="Build"/>. The escaped correlation key never contains
    /// '/', so the last '/' separates it from the message name.
    /// </summary>
    public static (string MessageName, string CorrelationKey) Parse(string key)
    {
        var separator = key.LastIndexOf('/');
        if (separator < 0)
            throw new ArgumentException($"'{key}' is not a message correlation key.", nameof(key));
        return (key[..separator], Uri.UnescapeDataString(key[(separator + 1)..]));
    }
}
