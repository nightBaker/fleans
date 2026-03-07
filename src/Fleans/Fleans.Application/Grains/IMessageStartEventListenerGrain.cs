using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IMessageStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);
    ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables);
}
