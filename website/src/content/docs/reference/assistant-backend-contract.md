---
title: Assistant Backend Contract
description: The HTTP + server-sent-events contract a service implements to power the BPMN editor's AI assistant panel.
sidebar:
  order: 3
---

The BPMN editor's [AI assistant panel](/fleans/guides/editor/#ai-assistant) talks to an **assistant
backend** that you run. Fleans ships the panel, the client and this contract — not the backend, and not a
model. Any service that implements the call below, in any language, can be plugged in with
`Fleans:Assistant:Endpoint`. .NET backends can reference the
[`Fleans.Assistant.Contracts`](https://www.nuget.org/packages/Fleans.Assistant.Contracts) package, which
holds the records and names on this page; it is released with every Fleans version.

## The call

```http
POST {endpoint}/assistant/chat
Content-Type: application/json
Accept: text/event-stream
Authorization: Bearer <user's access token>     (only with Admin UI authentication on)
```

By default the editor is **stateless towards the backend**: every call carries the whole conversation, the
open diagram, the custom-task plugins and the engine version, so a backend needs no storage. A backend that
keeps chats can say so and receive only the new turn instead — see [Stored chats](#stored-chats-optional).

### Request body

Property names are camelCase.

```json
{
  "messages": [
    { "role": "user", "text": "approval process with a 4h escalation timer" },
    { "role": "assistant", "text": "Here is a draft.\n\n```xml\n<definitions …/>\n```" },
    { "role": "user", "text": "add a timer to the approval task" }
  ],
  "currentXml": "<?xml version=\"1.0\"?><bpmn:definitions …>…</bpmn:definitions>",
  "plugins": [
    {
      "taskType": "rest-call",
      "displayName": "REST call",
      "parameterSchemaJson": "{\"parameters\":[{\"name\":\"url\",\"displayName\":\"URL\",\"type\":\"String\",\"required\":true,\"description\":null,\"defaultValue\":null,\"itemType\":null}]}"
    }
  ],
  "engineVersion": "0.11.0"
}
```

| Field | Meaning |
|---|---|
| `messages` | The conversation, oldest first; the last entry is the new user request. `role` is `user` or `assistant`. Only the **latest** proposal travels in full, as a fenced `xml` block in its assistant message; earlier ones are replaced by `[BPMN proposal omitted]`. |
| `currentXml` | The BPMN XML of the editor tab the user is in, or `null` for a blank diagram. Treat a non-null value as the diagram to edit. |
| `plugins` | The custom-task types registered on the cluster that the open diagram's process may use (the editor's plugin catalog; [private plugins](/fleans/concepts/custom-tasks/#categories-and-private-plugins) only if the process is on their allow-list). A proposal uses one as `<bpmn:serviceTask>` with `<fleans:taskDefinition type="…"/>` and fills its inputs from `parameterSchemaJson` — the same JSON as the `parameterSchema` of [`GET /custom-tasks`](/fleans/reference/api/). `null` when the plugin declares no parameters. |
| `engineVersion` | The Fleans version, so a backend can match the BPMN subset it generates to the engine. |
| `conversationId` | Optional. Only sent to a backend that [stores chats](#stored-chats-optional): the chat this turn belongs to. Then `messages` holds just the new turn. |

### Response: `text/event-stream`

Answer `200` with `Content-Type: text/event-stream` and any number of these events; each `data:` line is
one JSON object.

| `event:` | `data:` | Meaning |
|---|---|---|
| `text` | `{"text":"…"}` | The next piece of the reply, shown as it arrives. |
| `proposal` | `{"bpmnXml":"…"}` | A complete BPMN 2.0 diagram. **At most one per response** (the editor keeps the first). |
| `error` | `{"code":"…","message":"…"}` | The backend can't answer. Ends the response; `message` is shown to the user. |

```text
event: text
data: {"text":"Here is an approval process "}

event: text
data: {"text":"with a 4 h escalation."}

event: proposal
data: {"bpmnXml":"<?xml version=\"1.0\" encoding=\"UTF-8\"?><definitions …>…</definitions>"}
```

Other event names are ignored, so a backend can add its own.

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
response, event stream included. It never retries a call on its own.

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
what a backend that only implements `/assistant/chat` answers — everything stays as described above.
Unknown feature names are ignored. Every call carries the same `Authorization` header as the chat, and a
user only ever sees their own chats.

| Call | Answer | Used when |
|---|---|---|
| `POST /assistant/conversations` | `201` `{"conversationId":"…","title":null}` | The user sends the first message of a new chat. |
| `GET /assistant/conversations?cursor=` | `{"items":[{"conversationId","title","createdAt","updatedAt"}],"nextCursor":null}`, most recently updated first | The user opens **Chats**. |
| `GET /assistant/conversations/{id}` | `{"conversation":{…},"messages":[{"seq","role","text","bpmnXml","createdAt"}]}`, oldest first | A tab is restored after a reload, or the user picks a chat. |
| `DELETE /assistant/conversations/{id}` | `204` | The user deletes the chat. |

In a stored chat each `POST /assistant/chat` carries `conversationId` and only the new turn in `messages`
(including the editor's own `fix: …` requests); the backend adds both the turn and its reply — with the
proposal as `bpmnXml` — to the chat. It may start the response with `event: conversation`,
`data: {"conversationId":"…","title":"…"}`; the editor ignores it. A chat that does not exist (deleted,
expired, another user's) is a `404` with `{"code":"conversation_not_found","message":"…"}`: the editor
forgets it and the user's next message starts a new chat.

When it reads a chat back, the editor checks every stored proposal with the engine's BPMN converter
again, so only diagrams that pass get the apply buttons — the same rule as for a live reply. A stored user
message that starts with `fix: ` is shown as the editor's automatic fix request.

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

- Limit every answer to what the caller may read, using the same forwarded token as the chat. Nothing is
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
history stays in the browser and is never sent to the backend. **Nothing is deployed from the assistant** —
deploying stays a human action in the editor's deploy dialog.

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
proposals on its side; deleting a chat in the panel asks it to remove them. Searches in the
[Knowledge tab](#knowledge-index-optional) go to your backend as well; the Admin UI logs only which endpoint
was called and its status, never the query.

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
    var xml = await GenerateBpmnAsync(request, ct);   // call your model here
    yield return Event(AssistantProtocol.ProposalEvent, new AssistantProposal(xml));
}

static SseItem<string> Event<T>(string type, T data) =>
    new(JsonSerializer.Serialize(data, AssistantProtocol.JsonOptions), type);
```
