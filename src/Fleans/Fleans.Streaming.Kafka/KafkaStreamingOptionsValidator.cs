using Microsoft.Extensions.Options;

namespace Fleans.Streaming.Kafka;

/// <summary>Runs <see cref="KafkaQueueAdapterFactory.ValidateOptions"/> at host start for one named provider.</summary>
internal sealed class KafkaStreamingOptionsValidator(string name) : IValidateOptions<KafkaStreamingOptions>
{
    public ValidateOptionsResult Validate(string? optionsName, KafkaStreamingOptions options)
    {
        if (!string.Equals(optionsName, name, StringComparison.Ordinal))
            return ValidateOptionsResult.Skip;
        try
        {
            KafkaQueueAdapterFactory.ValidateOptions(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
