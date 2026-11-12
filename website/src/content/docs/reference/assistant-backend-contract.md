---
title: Assistant Backend Contract
description: The AG-UI agent endpoint (plus optional stored-chat and knowledge calls) a service implements to power the BPMN editor's AI assistant panel.
sidebar:
  order: 3
---

The BPMN editor's [AI assistant panel](/fleans/guides/editor/#ai-assistant) talks to an **assistant
backend** that you run. Fleans ships the panel, the client and this contract — not the backend, and not a
model. The run is the open [AG-UI protocol](https://docs.ag-ui.com) (Agent–User Interaction), so any AG-UI
agent — in any language, or built with an AG-UI framework — can be plugged in with `Fleans:Assistant:Endpoint`
once it follows the few Fleans conventions below. .NET backends can reference the
[`Fleans.Assistant.Contracts`](https://www.nuget.org/packages/Fleans.Assistant.Contracts) package, which
builds on `AGUI.Abstractions` and holds the conventions and records on this page; it is released with every
Fleans version.

## The call

```http
POST {endpoint}/assistant/agent
Content-Type: application/json
Accept: text/event-stream
Authorization: Bearer <user's access token>     (only with Admin UI authentication on)
```

The body is an AG-UI [`RunAgentInput`](https://docs.ag-ui.com/sdk/dotnet/abstractions/types#runagentinput) and the
answer an AG-UI [event stream](https://docs.ag-ui.com/concepts/events). By default the editor is **stateless towards
the backend**: every run carries the whole conversation, the open diagram, the custom-task plugins and the engine
version, so a backend needs no storage. A backend that keeps chats can say so and receive only the new turn instead —
see [Stored chats](#stored-chats-optional).

### Request body

```json
{
  "threadId": "5f0c…",
  "runId": "9a1e…",
  "protocolVersion": "1.0",
  "messages": [
    { "id": "…", "role": "user", "content": "approval process with a 4h escalation timer" },
    { "id": "…", "role": "assistant", "content": "Here is a draft.\n\n```xml\n<definitions …/>\n```" },
    { "id": "…", "role": "user", "content": "add a timer to the approval task" }
  ],
  "state": { "bpmnXml": "<?xml version=\"1.0\"?><bpmn:definitions …>…</bpmn:definitions>" },
  "context": [
    { "description": "fleans.engineVersion", "value": "0.12.0" },
    { "description": "fleans.plugins", "value": "[{\"taskType\":\"rest-call\",\"displayName\":\"REST call\",\"parameterSchemaJson\":\"{…}\"}]" }
  ],
  "tools": [
    {
      "name": "propose_bpmn_diagram",
      "description": "Propose a complete BPMN 2.0 diagram. …",
      "parameters": { "type": "object", "properties": { "bpmnXml": { "type": "string" } }, "required": ["bpmnXml"] }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `threadId` | A fresh id per run, or — for a backend that [stores chats](#stored-chats-optional) — the chat this turn belongs to. `runId` is always fresh. |
| `messages` | The conversation, oldest first; the last entry is the new user request. Only the **latest** proposal travels in full, as a fenced `xml` block in its assistant message; earlier ones are replaced by `[BPMN proposal omitted]`. In a stored chat it holds just the new turn. |
| `state.bpmnXml` | The BPMN XML of the editor tab the user is in; absent for a blank diagram. Treat it as the diagram to edit. |
| `context` `fleans.plugins` | A JSON array of the custom-task types registered on the cluster that the open diagram's process may use (the editor's plugin catalog; [private plugins](/fleans/concepts/custom-tasks/#categories-and-private-plugins) only if the process is on their allow-list). A proposal uses one as `<bpmn:serviceTask>` with `<fleans:taskDefinition type="…"/>` and fills its inputs from `parameterSchemaJson` — the same JSON as the `parameterSchema` of [`GET /custom-tasks`](/fleans/reference/api/). `null` when the plugin declares no parameters. |
| `context` `fleans.engineVersion` | The Fleans version, so a backend can match the BPMN subset it generates to the engine. |
| `tools` | The frontend tool `propose_bpmn_diagram`, which is how a backend hands the editor a diagram (below). |
| `resume` | Optional. The user's answers to [approval requests](#actions-approvals-and-jobs-optional). |

### Response: an AG-UI event stream

Answer `200` with `Content-Type: text/event-stream`; each `data:` line is one AG-UI event. The editor uses:

| Event | The panel shows |
|---|---|
| `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_CHUNK` | The reply, as it arrives. |
| `TOOL_CALL_START` / `TOOL_CALL_ARGS` / `TOOL_CALL_END` of **`propose_bpmn_diagram`**, arguments `{"bpmnXml":"…"}` | A complete BPMN 2.0 diagram. **At most one per run** (the editor keeps the first). It is a frontend tool: the editor never sends a result back. |
| `RUN_ERROR` `{"code","message"}` | The backend can't answer. Ends the run; `message` is shown to the user. |

```text
data: {"type":"RUN_STARTED","threadId":"5f0c…","runId":"9a1e…"}

data: {"type":"TEXT_MESSAGE_START","messageId":"m1","role":"assistant"}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"Here is an approval process "}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"with a 4 h escalation."}

data: {"type":"TEXT_MESSAGE_END","messageId":"m1"}

data: {"type":"TOOL_CALL_START","toolCallId":"p1","toolCallName":"propose_bpmn_diagram"}

data: {"type":"TOOL_CALL_ARGS","toolCallId":"p1","delta":"{\"bpmnXml\":\"<?xml version=\\\"1.0\\\"?><definitions …>…</definitions>\"}"}

data: {"type":"TOOL_CALL_END","toolCallId":"p1"}

data: {"type":"RUN_FINISHED","threadId":"5f0c…","runId":"9a1e…","outcome":{"type":"success"}}
```

Other events are ignored, including event types the editor does not know, so any AG-UI agent's stream is
accepted as is.

**Diagram interchange is optional.** A proposal may contain just `<process>` elements and sequence flows;
the editor lays out elements without `bpmndi` shapes and draws missing edges.

### Status codes

| Status | Shown in the panel as |
|---|---|
| `401` | The forwarded token was missing, expired or rejected — "sign in again". |
| `429` | Rate limited — "try again in a moment". |
| `5xx`, connection failure | The assistant service is unavailable. |
| any other non-success | The assistant could not answer. |

A non-success response may carry `{"code":"…","message":"…"}` as its JSON body; the panel shows the
`message`. The editor also gives up after `Fleans:Assistant:Timeout` (default 2 minutes) for the whole
response, event stream included. It never retries a run on its own.

## Stored chats (optional)

A backend may keep each user's chats, so a tab's chat survives a page reload and the user can switch
between earlier ones. The editor asks once per session:

```http
GET {endpoint}/assistant/capabilities
```

```json
{ "contractVersion": 2, "features": ["bpmn", "persistence"] }
```

With `persistence` in `features`, the editor uses the calls below; without it — or on a `404`, which is
what a backend that only implements `/assistant/agent` answers — everything stays as described above.
Unknown feature names are ignored. Every call carries the same `Authorization` header as the run, and a
user only ever sees their own chats.

| Call | Answer | Used when |
|---|---|---|
| `POST /assistant/conversations` | `201` `{"conversationId":"…","title":null}` | The user sends the first message of a new chat. |
| `GET /assistant/conversations?cursor=` | `{"items":[{"conversationId","title","createdAt","updatedAt"}],"nextCursor":null}`, most recently updated first | The user opens **Chats**. |
| `GET /assistant/conversations/{id}` | `{"conversation":{…},"messages":[{"seq","role","text","bpmnXml","createdAt"}]}`, oldest first | A tab is restored after a reload, or the user picks a chat. |
| `PATCH /assistant/conversations/{id}` with `{"title":"…"}` | `200` | The user renames the chat. |
| `DELETE /assistant/conversations/{id}` | `204` | The user deletes the chat. |

In a stored chat each run's `threadId` is the chat's `conversationId`, and `messages` holds only the new turn
(including the editor's own `fix: …` requests); the backend adds both the turn and its reply — with the
proposal as `bpmnXml` — to the chat. A chat that does not exist (deleted,
expired, another user's) is a `404` with `{"code":"conversation_not_found","message":"…"}`: the editor
forgets it and the user's next message starts a new chat.

When it reads a chat back, the editor checks every stored proposal with the engine's BPMN converter
again, so only diagrams that pass get the apply buttons — the same rule as for a live reply. A stored user
message that starts with `fix: ` is shown as the editor's automatic fix request.

## Actions, approvals and jobs (optional)

A backend that stores chats may do more than propose diagrams, such as open a merge request or run a load
test. The panel shows what its agents do, asks the user before anything changes, and follows long-running jobs.
All of this is optional, and a backend sends these events only in a stored chat.

| AG-UI | The panel shows |
|---|---|
| `TOOL_CALL_START` of any tool other than `propose_bpmn_diagram`, then its `TOOL_CALL_RESULT` | One activity line per `toolCallId` (the tool's name, started → completed), for the latest turn. |
| `RUN_FINISHED` with `"outcome":{"type":"interrupt","interrupts":[…]}` | One approval card per interrupt: `message` is the summary, `reason` the action's kind, `metadata.detailsMarkdown` the details and `expiresAt` (ISO 8601) the expiry, with **Run** and **Cancel**. An interrupt without `expiresAt` never expires. |
| `CUSTOM` named `fleans.job`, `value` `{"jobId","kind","status","summary","url"}`, `status` is `queued`, `running`, `succeeded`, `failed` or `cancelled` | One card per `jobId`. A finished job stays finished. |

```text
data: {"type":"RUN_FINISHED","threadId":"…","runId":"…","outcome":{"type":"interrupt","interrupts":[{"id":"a1","reason":"deploy.apply","message":"Deploy shop to stage","expiresAt":"2026-11-12T10:10:00Z","metadata":{"detailsMarkdown":"**tag** 1.2"}}]}}
```

The details are rendered as Markdown with raw HTML escaped and only `http(s)` and `mailto` links kept. A job's
`url` becomes a link only if it is `http(s)`.

**Run** and **Cancel** start an ordinary run that [resumes](https://docs.ag-ui.com/concepts/interrupts) the interrupt,
so there is no separate path for a backend to protect:

```json
{
  "threadId": "…",
  "runId": "…",
  "messages": [{ "id": "…", "role": "user", "content": "" }],
  "state": { "bpmnXml": "…" },
  "context": [ … ],
  "tools": [ … ],
  "resume": [{ "interruptId": "a1", "status": "resolved", "payload": { "approved": true, "comment": null } }]
}
```

Answer it like any run: the panel adds the text as the assistant's reply. **The approval is enforced by the
backend, not by the panel.** Check on every answer that the action belongs to this user and chat, has not expired and has not
been answered, and run it at most once. The panel drops the card on success and on a `RUN_ERROR` with
`approval_invalid`, `approval_expired` or `forbidden_action`. After any other failure it keeps the card so the user can try
again, so a repeated answer must be harmless. It sends nothing for a card that has already expired.

### Between requests

While the panel is open it follows the chat with one background connection:

```http
GET {endpoint}/assistant/conversations/{id}/events
Last-Event-ID: 41
```

Answer `text/event-stream` with the same AG-UI events — `fleans.job` custom events, interrupts in a `RUN_FINISHED`, tool
calls — each with an SSE `id:`. You may end the stream at any
time: the editor reconnects about three seconds later and sends the last id it saw, and it also drops the connection
after `Fleans:Assistant:Timeout` and reconnects. A `401`, `403` or `404` stops the feed until the chat is opened again.
Other events are ignored, and an event with an unreadable `data:` is skipped.

Without `Last-Event-ID`, as after a page reload, send the chat's events from the start: that is how a reloaded panel
gets its job cards and pending approvals back. An approval that was already answered may be repeated this way until it
expires, and answering it again gets `approval_invalid`. Only events on this feed come back: an interrupt sent only
in a run's answer is gone after a reload, so also publish it here if it should survive one.

`POST {endpoint}/assistant/jobs/{jobId}/cancel` asks to stop a job. Answer `202`; the outcome arrives as a `fleans.job` event.

### Error codes

The panel has its own wording for these `code` values of a `RUN_ERROR`, and shows `message` next to it:
`forbidden_action`, `approval_expired`, `approval_invalid`, `integration_unavailable`, `job_failed` and `budget_exceeded`.

## Knowledge index (optional)

A backend that keeps a searchable index of your repositories can show it in the Admin UI's
[Knowledge tab](/fleans/guides/knowledge/). It lists `knowledge` in `features` of
`GET /assistant/capabilities` and serves these read-only calls. Without that feature — or on a backend
without `/assistant/capabilities` — the tab does not exist and none of these calls is made.

| Call | Answer |
|---|---|
| `GET /api/ai/knowledge/sources` | An array of source rows. |
| `GET /api/ai/knowledge/search?q=&project=&kind=` | An array of hit rows, best first. `kind` is `doc`, `adr`, `code` or `bpmn` and repeats. |
| `GET /api/ai/knowledge/files?project=&repo=&cursor=` | `{"items":[…],"nextCursor":"…"}` with file rows. `null` ends the listing. |
| `GET /api/ai/knowledge/chunks?repo=&path=` | `{"items":[…],"truncated":false}` with the file's hit rows. `truncated` says the file has more chunks than one answer returns. |
| `GET /api/ai/knowledge/adrs?project=&q=` | An array of hit rows; without `q`, one row per ADR. |

Parameters left unset are not sent. Rows, with camelCase names:

```json
{ "project": "payments", "repo": "bank/payments", "branch": "main", "commitSha": "abcdef12…",
  "engineVersion": "1.0.0", "indexedAt": "2026-11-05T10:00:00Z", "chunks": 42, "hasEmbeddings": true,
  "kinds": { "doc": 10, "adr": 2, "code": 28, "bpmn": 2 }, "webUrl": "https://git.example/bank/payments" }
```

```json
{ "project": "payments", "repo": "bank/payments", "branch": "main", "commitSha": "abcdef12…",
  "path": "docs/adr/0001-retry.md", "line": 1, "kind": "adr", "symbol": null, "title": "Retry policy",
  "content": "# Retry policy…", "score": 0.5 }
```

```json
{ "project": "payments", "repo": "bank/payments", "branch": "main", "commitSha": "abcdef12…",
  "path": "src/Pay.cs", "line": 1, "kind": "code", "title": "PayService", "chunks": 3 }
```

- Limit every answer to what the caller may read, using the same forwarded token as the run. Nothing is
  cached in the Admin UI, between users or otherwise.
- A repository, path or project the caller may not read, or knowledge that was turned off, is a `404`. The
  tab shows "not found"; a `404` on `sources` makes the whole tab disappear.
- `webUrl` is optional. With it, the tab links files to `{webUrl}/blob/{commitSha}/{path}#L{line}`, a form
  GitHub and GitLab both serve. Without it, or if it isn't `http(s)`, there are no links.
- `content` of a `doc` or `adr` is rendered as Markdown. Raw HTML is escaped, images become links, and only
  `http(s)` and `mailto` links are kept.

## What the editor does with a proposal

Every proposal goes through the engine's BPMN converter — the same check as a deploy. If it fails, the
editor sends one more request on its own, with the user message `fix: <conversion error>` (the rejected
XML is in the previous assistant message), up to **twice**. A proposal that still fails is shown as an
error; only XML that converts reaches the user, whatever the backend returns. By default the editor applies
it to the open diagram straight away, and the user can undo it or open it in a new tab. The tab's change
history stays in the browser and is never sent to the backend. **A diagram is never deployed from the assistant** —
deploying it stays a human action in the editor's deploy dialog.

## Authentication

With the Admin UI's [OIDC sign-in](/fleans/reference/authentication/) on, the editor sends the signed-in
user's access token (issued to the Admin UI's client, `Authentication:ClientId`) as
`Authorization: Bearer`. Validate it against the same issuer. If your backend checks the token's audience,
add it to the Admin UI client's access tokens (in Keycloak, an audience mapper on that client), along with
any claims it authorizes on, such as `groups`. Fleans does not refresh it: once it expires
the backend should answer `401`, and the user reloads the page to sign in again. Without authentication
no `Authorization` header is sent; protect the backend at the network level instead.

## Privacy

The feature is opt-in: without `Fleans:Assistant:Endpoint` the panel is hidden and nothing is sent.
Once configured, the user's messages, the open diagram and the plugin catalog (task types, display names,
parameter schemas) go to your backend, and from there to whatever model provider it uses. The Admin UI
logs only the shape of each call (message count, whether a diagram was sent, plugin count, duration) —
never prompts or diagrams. A backend that [stores chats](#stored-chats-optional) keeps the messages and
proposals on its side; deleting a chat in the panel asks it to remove them. Activity lines, approval details and job
summaries are shown to the user and never logged by the Admin UI, which records only an approval's `kind`, the decision and
any error code. Searches in the
[Knowledge tab](#knowledge-index-optional) go to your backend as well; the Admin UI logs only which endpoint
was called and its status, never the query.

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
    // The conversation, the open diagram, the plugins and the engine version, as the editor sent them.
    var request = AssistantAgUi.FromRunAgentInput(input);

    yield return Event(new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId });
    yield return Event(new TextMessageStartEvent { MessageId = "m1", Role = AGUIRoles.Assistant });
    yield return Event(new TextMessageContentEvent { MessageId = "m1", Delta = "Here is a draft." });
    yield return Event(new TextMessageEndEvent { MessageId = "m1" });

    var xml = await GenerateBpmnAsync(request, ct);   // call your model here
    foreach (var proposal in AssistantAgUi.ProposalEvents(xml))   // the propose_bpmn_diagram tool call
        yield return Event(proposal);

    yield return Event(new RunFinishedEvent
    {
        ThreadId = input.ThreadId, RunId = input.RunId, Outcome = new RunFinishedSuccessOutcome(),
    });
}

static SseItem<string> Event(BaseEvent evt) =>
    new(JsonSerializer.Serialize(evt, AGUIJsonSerializerContext.Default.BaseEvent));
```

An agent built on an AG-UI framework works the same way: give the model the `propose_bpmn_diagram` tool the editor
sends in `tools`, the diagram from `state` and the two `fleans.*` `context` entries, and let it call the tool once it has
a diagram. `AssistantAgUi` also builds the approval interrupt (`ToInterrupt`), reads its answer (`FromResume`) and builds
the job event (`ToJobEvent`).
