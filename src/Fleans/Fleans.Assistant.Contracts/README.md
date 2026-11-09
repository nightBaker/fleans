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
and the engine version on every call, so a backend is stateless — unless it stores chats (below). Non-success statuses: `401`
(token rejected), `429` (rate limited), `5xx` (unavailable).

## Stored chats (optional)

A backend that keeps chats answers `GET {endpoint}/assistant/capabilities` with
`AssistantCapabilities` listing `persistence` (`AssistantProtocol.PersistenceFeature`). The editor then
creates a chat with `POST /assistant/conversations` (`AssistantConversation`), sends
`AssistantRequest.ConversationId` with only the new turn in `Messages`, reads chats back with
`GET /assistant/conversations` (`AssistantConversationPage`) and `GET /assistant/conversations/{id}`
(`AssistantConversationHistory`), renames them with `PATCH` (`AssistantConversationUpdate`) and deletes them
with `DELETE`. A missing chat is a `404` with `AssistantErrorCodes.ConversationNotFound`. A backend without
`/assistant/capabilities` (404) keeps the stateless call above.

## Actions, approvals and jobs (optional)

In a stored chat a backend may report what its agents do and ask before it changes anything:

| `event:` | `data:` | Meaning |
|---|---|---|
| `activity` | `AssistantActivity` | An agent started or finished using a tool. The same `id` updates one line. |
| `approval-required` | `AssistantApprovalRequest` | An action waits for the user's Run or Cancel. |
| `job` | `AssistantJobUpdate` | A long-running job changed state. The same `jobId` updates one card. |

An answer is an ordinary `POST /assistant/chat` with `AssistantRequest.Approvals` (`AssistantApprovalDecision`), the
chat's id and one user message with an empty text. `GET /assistant/conversations/{id}/events` delivers what happens
between requests as `text/event-stream`, with an `id:` on each event, resuming from `Last-Event-ID`.
`POST /assistant/jobs/{id}/cancel` asks to stop a job. Clients ignore event names and features they do not know.
Details: [actions, approvals and jobs](https://nightbaker.github.io/fleans/reference/assistant-backend-contract/#actions-approvals-and-jobs-optional).

## Knowledge index (optional)

A backend with a knowledge index lists `knowledge` (`AssistantProtocol.KnowledgeFeature`) in its capabilities
and serves read-only `GET {endpoint}/api/ai/knowledge/{sources,search,files,chunks,adrs}`, answering
`KnowledgeSource`, `KnowledgeHit`, `KnowledgeFilePage` and `KnowledgeChunks`. The Admin UI then shows a
**Knowledge** tab. Details: [knowledge index](https://nightbaker.github.io/fleans/reference/assistant-backend-contract/#knowledge-index-optional).

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
