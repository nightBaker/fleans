# Fleans.Assistant.Contracts

The HTTP contract between the Fleans BPMN editor's **AI assistant panel** and an assistant
backend. Fleans ships the panel and its client, not a backend: any service that implements this
contract can be plugged in with `Fleans:Assistant:Endpoint`. The package holds only plain records
and constants, so a .NET backend can reference it; backends in other languages follow the JSON
shapes in the [assistant backend contract](https://nightbaker.github.io/fleans/reference/assistant-backend-contract/).

## The call

`POST {endpoint}/assistant/chat` with an `AssistantRequest` (JSON, camelCase), answered with
`text/event-stream`:

| `event:` | `data:` | Meaning |
|---|---|---|
| `text` | `AssistantTextDelta` `{"text":"…"}` | The next piece of the reply. |
| `proposal` | `AssistantProposal` `{"bpmnXml":"…"}` | A complete BPMN diagram, at most one per response. |
| `error` | `AssistantError` `{"code":"…","message":"…"}` | The backend could not answer; ends the response. |

The editor sends the whole conversation, the active diagram, the registered custom-task plugins
and the engine version on every call, so a backend is stateless. Non-success statuses: `401`
(token rejected), `429` (rate limited), `5xx` (unavailable).

## Minimal ASP.NET Core backend

```csharp
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fleans.Assistant.Contracts;

var app = WebApplication.CreateBuilder(args).Build();

app.MapPost("/" + AssistantProtocol.ChatPath, (AssistantRequest request, CancellationToken ct) =>
    TypedResults.ServerSentEvents(Answer(request, ct)));

app.Run();

static async IAsyncEnumerable<SseItem<string>> Answer(
    AssistantRequest request, [EnumeratorCancellation] CancellationToken ct)
{
    yield return Event(AssistantProtocol.TextEvent, new AssistantTextDelta("Here is a draft."));
    var xml = await GenerateBpmnAsync(request, ct);   // your model call
    yield return Event(AssistantProtocol.ProposalEvent, new AssistantProposal(xml));
}

static SseItem<string> Event<T>(string type, T data) =>
    new(JsonSerializer.Serialize(data, AssistantProtocol.JsonOptions), type);
```

Proposals may omit diagram interchange (`bpmndi`): the editor lays out a bare `<process>`. The
editor validates every proposal with the engine's BPMN converter and, if it fails, asks the
backend to fix it (`fix: <conversion error>`) up to twice before showing the error.
