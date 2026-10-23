using System.Dynamic;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface IMessageStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);

    // [AlwaysInterleave] (#858): one listener per message name, so a serialized fire would queue every
    // start of that message behind the one in flight. Safe because the fire copies the listener state
    // before its first await and never writes it; RegisterProcess/UnregisterProcess stay serialized.
    [AlwaysInterleave]
    ValueTask<List<Guid>> FireMessageStartEvent(ExpandoObject variables, string? businessId = null);
}
