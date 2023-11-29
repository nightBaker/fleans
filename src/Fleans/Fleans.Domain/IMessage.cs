namespace Fleans.Domain;

public interface IMessage
{
    string CorrelationKey { get; set; }
}