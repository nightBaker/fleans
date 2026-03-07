namespace Fleans.Application.Grains;

public interface ISignalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);
    ValueTask<List<Guid>> FireSignalStartEvent();
}
