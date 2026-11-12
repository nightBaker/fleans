# Fleans.Assistant.Contracts

The contract between the Fleans BPMN editor's **AI assistant panel** and an assistant backend. Fleans ships
the panel and its client, not a backend: any service that implements this contract can be plugged in with
`Fleans:Assistant:Endpoint`. The run itself is the open [AG-UI protocol](https://docs.ag-ui.com)
(`AGUI.Abstractions`); this package adds the few Fleans conventions on top (`AssistantAgUi`) and the records of
the optional stored-chat and knowledge calls. Backends in other languages follow the JSON shapes in the
[assistant backend contract](https://nightbaker.github.io/fleans/reference/assistant-backend-contract/).

## The run

`POST {endpoint}/assistant/agent` (`AssistantProtocol.AgentPath`) with an AG-UI `RunAgentInput`, answered with an
AG-UI `text/event-stream`. `AssistantAgUi.ToRunAgentInput` / `FromRunAgentInput` convert it to and from the
editor's `AssistantRequest`:

| AG-UI | Fleans meaning |
|---|---|
| `messages` | The conversation (user / assistant text). |
| `state.bpmnXml` | The open diagram; absent for a blank one. |
| `context` `fleans.engineVersion`, `fleans.plugins` | The engine version and the JSON array of `AssistantPlugin`s. |
| `tools` `propose_bpmn_diagram` | The frontend tool a backend calls with `{"bpmnXml":"…"}` to propose a diagram (`AssistantAgUi.ProposalEvents`). |
| `threadId` | The stored chat, or a fresh id per run. |
| `resume` | The user's answers to approvals (`AssistantAgUi.ToResume` / `FromResume`). |

The editor reads `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_CHUNK` (the reply), the `propose_bpmn_diagram` tool call
(the proposal, at most one per run) and `RUN_ERROR` (`code`, `message`). Other events are ignored. Non-success
statuses: `401` (token rejected), `429` (rate limited), `5xx` (unavailable), optionally with an `AssistantError` body.

## Stored chats (optional)

A backend that keeps chats answers `GET {endpoint}/assistant/capabilities` with
`AssistantCapabilities` listing `persistence` (`AssistantProtocol.PersistenceFeature`). The editor then
creates a chat with `POST /assistant/conversations` (`AssistantConversation`), runs with the chat's id as
`threadId` and only the new turn in `messages`, reads chats back with `GET /assistant/conversations`
(`AssistantConversationPage`) and `GET /assistant/conversations/{id}` (`AssistantConversationHistory`), renames
them with `PATCH` (`AssistantConversationUpdate`) and deletes them with `DELETE`. A missing chat is a `404` with
`AssistantErrorCodes.ConversationNotFound`. A backend without `/assistant/capabilities` (404) keeps the stateless run above.

## Actions, approvals and jobs (optional)

| AG-UI | The panel shows |
|---|---|
| `TOOL_CALL_START` / `TOOL_CALL_RESULT` of any other tool | An activity line per `toolCallId` (`AssistantActivity`). |
| `RUN_FINISHED` with an `interrupt` outcome | An approval card per interrupt (`AssistantAgUi.ToInterrupt` / `FromInterrupt`). |
| `CUSTOM` named `fleans.job` | A job card (`AssistantAgUi.ToJobEvent`, value `AssistantJobUpdate`). |

Run or Cancel is an ordinary run with `resume` entries and one user message with an empty text.
`GET /assistant/conversations/{id}/events` delivers the same AG-UI events between runs, each with an SSE `id:`,
resuming from `Last-Event-ID`. `POST /assistant/jobs/{id}/cancel` asks to stop a job.
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
using AGUI.Abstractions;
using Fleans.Assistant.Contracts;

var app = WebApplication.CreateBuilder(args).Build();

app.MapPost("/" + AssistantProtocol.AgentPath, async (HttpRequest http, CancellationToken ct) =>
{
    var input = await JsonSerializer.DeserializeAsync(http.Body, AGUIJsonSerializerContext.Default.RunAgentInput, ct);
    return TypedResults.ServerSentEvents(Answer(input!, ct));
});

app.Run();

static async IAsyncEnumerable<SseItem<string>> Answer(
    RunAgentInput input, [EnumeratorCancellation] CancellationToken ct)
{
    var request = AssistantAgUi.FromRunAgentInput(input);
    yield return Event(new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId });
    yield return Event(new TextMessageStartEvent { MessageId = "m1", Role = AGUIRoles.Assistant });
    yield return Event(new TextMessageContentEvent { MessageId = "m1", Delta = "Here is a draft." });
    yield return Event(new TextMessageEndEvent { MessageId = "m1" });
    var xml = await GenerateBpmnAsync(request, ct);   // your model call
    foreach (var proposal in AssistantAgUi.ProposalEvents(xml))
        yield return Event(proposal);
    yield return Event(new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId, Outcome = new RunFinishedSuccessOutcome() });
}

static SseItem<string> Event(BaseEvent evt) =>
    new(JsonSerializer.Serialize(evt, AGUIJsonSerializerContext.Default.BaseEvent));
```

Proposals may omit diagram interchange (`bpmndi`): the editor lays out a bare `<process>`. The
editor validates every proposal with the engine's BPMN converter and, if it fails, asks the
backend to fix it (`fix: <conversion error>`) up to twice before showing the error.
