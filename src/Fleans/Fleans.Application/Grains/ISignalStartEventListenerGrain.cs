using Orleans.Concurrency;

namespace Fleans.Application.Grains;

public interface ISignalStartEventListenerGrain : IGrainWithStringKey
{
    ValueTask RegisterProcess(string processDefinitionKey);
    ValueTask UnregisterProcess(string processDefinitionKey);

    // [AlwaysInterleave] (#858): one listener per signal name, so a serialized fire would queue every
    // start of that signal behind the one in flight. Safe because the fire copies the listener state
    // before its first await and never writes it; RegisterProcess/UnregisterProcess stay serialized.
    [AlwaysInterleave]
    ValueTask<List<Guid>> FireSignalStartEvent();
}
