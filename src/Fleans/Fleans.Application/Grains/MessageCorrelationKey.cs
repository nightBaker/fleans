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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        var key = $"{messageName}/{Uri.EscapeDataString(correlationKey)}";
        if (key.Length > MaxLength)
            throw new ArgumentException(
                $"Message name plus escaped correlation key is {key.Length} characters; the limit is {MaxLength}.",
                nameof(correlationKey));
        return key;
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
