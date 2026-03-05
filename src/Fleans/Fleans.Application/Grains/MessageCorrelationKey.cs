namespace Fleans.Application.Grains;

public static class MessageCorrelationKey
{
    public static string Build(string messageName, string correlationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        return $"{messageName}/{Uri.EscapeDataString(correlationKey)}";
    }
}
