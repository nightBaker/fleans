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

The editor is **stateless towards the backend**: every call carries the whole conversation, the open
diagram, the custom-task plugins and the engine version. A backend needs no storage.

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
| `plugins` | The custom-task types registered on the cluster (the editor's plugin catalog). A proposal uses one as `<bpmn:serviceTask>` with `<fleans:taskDefinition type="…"/>` and fills its inputs from `parameterSchemaJson` — the same JSON as the `parameterSchema` of [`GET /custom-tasks`](/fleans/reference/api/). `null` when the plugin declares no parameters. |
| `engineVersion` | The Fleans version, so a backend can match the BPMN subset it generates to the engine. |

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

## What the editor does with a proposal

Every proposal goes through the engine's BPMN converter — the same check as a deploy. If it fails, the
editor sends one more request on its own, with the user message `fix: <conversion error>` (the rejected
XML is in the previous assistant message), up to **twice**. A proposal that still fails is shown as an
error; only XML that converts is offered to the user, whatever the backend returns. The user then opens
it in a new tab or replaces the open diagram. **Nothing is deployed from the assistant** — deploying stays
a human action in the editor's deploy dialog.

## Authentication

With the Admin UI's [OIDC sign-in](/fleans/reference/authentication/) on, the editor sends the signed-in
user's access token (issued to the Admin UI's client, `Authentication:ClientId`) as
`Authorization: Bearer`. Validate it against the same issuer. Fleans does not refresh it: once it expires
the backend should answer `401`, and the user reloads the page to sign in again. Without authentication
no `Authorization` header is sent; protect the backend at the network level instead.

## Privacy

The feature is opt-in: without `Fleans:Assistant:Endpoint` the panel is hidden and nothing is sent.
Once configured, the user's messages, the open diagram and the plugin catalog (task types, display names,
parameter schemas) go to your backend, and from there to whatever model provider it uses. The Admin UI
logs only the shape of each call (message count, whether a diagram was sent, plugin count, duration) —
never prompts or diagrams.

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
