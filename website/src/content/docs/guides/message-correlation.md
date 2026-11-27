---
title: Message Correlation
description: How Fleans correlates incoming BPMN messages to running workflow instances — BPMN definition, variable resolution semantics, the message API, and a curl-driven cookbook.
---


A **message correlation key** is the runtime value that routes an incoming message to the right workflow instance. Without it, every workflow waiting on `approvalReceived` would wake up — with it, only the one whose `requestId == "req-456"` does. This guide covers what a correlation key is in BPMN, how Fleans parses it, how the engine resolves it at runtime, the `POST /Execution/message` API, and a small cookbook of the three patterns you'll most often need.

## What is a correlation key?

In BPMN, a **message** is a typed payload that flows between processes. When a workflow contains a `<intermediateCatchEvent>` or an event sub-process triggered by `<messageEventDefinition>`, that workflow *subscribes* to a (message-name, correlation-key) pair and pauses until something delivers a matching one.

The correlation **name** is static and lives on the `<bpmn:message>` definition. The correlation **key value** is dynamic — it is read from the workflow's variables at the moment the subscription is registered. So a workflow with `requestId = "req-456"` waiting on the message `approvalReceived` registers a subscription under the key `approvalReceived/req-456`. A `POST /Execution/message` with `MessageName="approvalReceived"`, `CorrelationKey="req-456"` matches that exact subscription and resumes the workflow.

The grain layer enforces a **single subscriber per (messageName, correlationKey) pair** — two instances cannot wait on the same key at the same time. This is what gives correlation its routing power: the key is the address.

## BPMN definition

The canonical XML shape, from the sample [`message-catch.bpmn`](/fleans/samples/message-catch.bpmn) (download it — the curl walkthrough below deploys it):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:fleans="https://fleans.io/schema/bpmn/1.0">
  <message id="msg1" name="approvalReceived">
    <extensionElements>
      <fleans:subscription correlationKey="= requestId" />
    </extensionElements>
  </message>
  <process id="message-catch-test" isExecutable="true">
    <startEvent id="start" />
    <scriptTask id="setCorrelation" scriptFormat="csharp">
      <script>_context.requestId = "req-456"</script>
    </scriptTask>
    <intermediateCatchEvent id="waitApproval">
      <messageEventDefinition messageRef="msg1" />
    </intermediateCatchEvent>
    <scriptTask id="afterApproval" scriptFormat="csharp">
      <script>_context.approved = true</script>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setCorrelation" />
    <sequenceFlow id="f2" sourceRef="setCorrelation" targetRef="waitApproval" />
    <sequenceFlow id="f3" sourceRef="waitApproval" targetRef="afterApproval" />
    <sequenceFlow id="f4" sourceRef="afterApproval" targetRef="end" />
  </process>
</definitions>
```

Three load-bearing details:

1. **The `xmlns:fleans="https://fleans.io/schema/bpmn/1.0"` namespace declaration** must appear on `<definitions>`. Without it, `<fleans:subscription>` is parsed as an unknown element and silently ignored (no correlation key, no error). Files exported from Camunda's modeler may use `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` instead — that's also accepted via Fleans's back-compat probe order.
2. **The `=` prefix** is the Zeebe expression marker. Fleans strips leading `=` and space characters at parse time and treats the remainder as a single variable name. The engine does **not** evaluate arbitrary expressions — see [Variable resolution semantics](#variable-resolution-semantics) below.
3. **Placement of `<extensionElements>` is project-specific** — read the caution that follows.

:::caution[Place `<extensionElements>` inside `<bpmn:message>`, not inside the message-event element]
Fleans only reads a `<fleans:subscription>` (or `<zeebe:subscription>`) that sits in the `<extensionElements>` **directly under `<bpmn:message>`** (`BpmnConverter.ParseMessages`). Putting the `<fleans:subscription>` block under the `<intermediateCatchEvent>` or the `<startEvent>` is **silently ignored** — the engine treats the message as "no correlation key" and your `POST /Execution/message` will never match.

The one alternative the parser accepts is a `fleans:correlationKey="requestId"` **attribute** on the `<intermediateCatchEvent>` or `<boundaryEvent>` that references the message; it is used only when the `<bpmn:message>` has no subscription.

This is the single most common authoring mistake. Compare with the [`message-catch.bpmn`](/fleans/samples/message-catch.bpmn) sample before authoring a new message-event workflow.
:::

The same shape applies whether the consumer is an `<intermediateCatchEvent>` or an event-sub-process `<startEvent>`; only the consuming element changes. See the [`message-event-subprocess.bpmn`](https://github.com/nightBaker/fleans/blob/main/tests/manual/21-event-subprocess-message/message-event-subprocess.bpmn) fixture for the sub-process variant.

## Variable resolution semantics

When a workflow reaches a message-catch (or registers an event-sub-process subscription), the engine calls `WorkflowExecution.ResolveCorrelationKey`:

```csharp
private string ResolveCorrelationKey(MessageDefinition messageDef, Guid variablesId)
{
    if (messageDef.CorrelationKeyExpression is null)
        return string.Empty;

    var variableName = messageDef.CorrelationKeyExpression.StartsWith("= ")
        ? messageDef.CorrelationKeyExpression[2..]
        : messageDef.CorrelationKeyExpression;

    var correlationValue = _state.GetVariable(variablesId, variableName);
    return correlationValue?.ToString()
        ?? throw new InvalidOperationException(
            $"Correlation variable '{variableName}' is null for message '{messageDef.Name}'.");
}
```

What this means in practice:

- **The `=` prefix is stripped, then the remainder is treated as a plain variable name** — `_state.GetVariable(...)` is a dictionary lookup against the workflow's `ExpandoObject`-backed variable scope, not a Roslyn or DynamicExpresso evaluation. A correlation expression like `= requestId` looks up the variable named `requestId`. A correlation expression like `= a + b` looks up the literal variable named `a + b` (which doesn't exist) and throws.
- **Resolution scope depends on where the subscription is registered.** For an `<intermediateCatchEvent>`, resolution happens at the host activity's variable scope at the moment execution reaches the catch — variables set by upstream script tasks in the same scope (or any ancestor scope, see [Variables and Scope](/fleans/guides/variables-and-scope/)) are visible.
- **A null variable throws `InvalidOperationException`** with the message `Correlation variable '{name}' is null for message '{messageName}'.`. The throw aborts the workflow — there is no fallback to "empty correlation key". Either seed the correlation variable from the `/start` request's `Variables` payload, or set it via a script task that runs **before** the message-catch is reached.
- **Intermediate catch and boundary message events require a correlation key.** If the `<message>` has no `<fleans:subscription correlationKey>` (or the variable resolves to an empty string), the catch / boundary activity is **failed** with `Message '{name}' has no correlation key. ...` as soon as execution reaches it. Uncorrelated `POST /Execution/message` calls (empty `CorrelationKey`) only ever reach message **start** events.
- **The same resolution applies to message-event sub-processes**: their subscription is registered when the enclosing scope opens, against that scope's variables. They need a correlation key too: if the subscription can't be registered (no key, or another instance already waits on the same key), an event sub-process inside a sub-process fails that sub-process, and one at the process level fails the **whole instance** as soon as it starts.

For message **start** events, no correlation key lives on the BPMN definition (the `<message>` has no `<extensionElements>`). Routing for start events is by message *name* alone: when no running instance matches the request's `CorrelationKey`, each process with a matching message start event starts a fresh instance, seeded with the request's `Variables`. The `CorrelationKey` itself is not stored on the new instance — send any value the process needs (an order id, say) in `Variables`.

## API request shape

The endpoint is `POST /Execution/message`. The DTO is `SendMessageRequest` from `Fleans.ServiceDefaults`:

```csharp
public record SendMessageRequest(
    string MessageName, string? CorrelationKey, ExpandoObject? Variables,
    string? BusinessId = null, string? IdempotencyKey = null);
```

Field semantics:

- **`MessageName`** (required, **case-sensitive**) — must match the BPMN `<message name="...">` exactly. Capitalization mismatch returns 404.
- **`CorrelationKey`** (optional) — the runtime string the subscriber's correlation expression resolved to. May be omitted only for messages whose BPMN definition has no `<fleans:subscription>` (e.g. message start events with no key).
- **`Variables`** (optional) — extra variables to merge into the receiving workflow's scope before it resumes. Useful for delivering response payloads (e.g. an `approvalDecision` field).
- **`BusinessId`** (optional) — the business id of instances a message start event creates.
- **`IdempotencyKey`** (optional) — makes the request safe to retry: a repeat with the same `MessageName` and key within 24 hours returns the first request's outcome and starts nothing. Without it, a retry after a lost response starts the message-start processes again. See [Retrying a message](/fleans/reference/api/#retrying-a-message).

Responses:

- **200 OK** with `{"delivered": true, "workflowInstanceIds": [...]}` — `workflowInstanceIds` lists the affected instances (one for an intermediate catch, possibly multiple for start-event correlations). Like every response body, it is camelCase.
- **400 Bad Request** if `MessageName` is null or whitespace.
- **Key length limit:** `MessageName` plus the URL-escaped `CorrelationKey` (joined by `/`) may be at most 1024 characters, on every database provider. A catch or boundary event whose key is longer fails at subscribe time. A request with a longer key cannot match any subscription, so it goes straight to message start events (404 if there are none).
- **404 Not Found** with `{"error": "No subscription or start event found for message '...'"}` if no subscriber is waiting on that `(name, key)` pair and no process starts on that message. A subscription whose instance no longer waits on it (the catch already finished, its scope was cancelled, or the instance was purged) does not count as a delivery: it is cleared, and the request goes on to message start events — `200` with the started instances if one matches, otherwise `404`. One exception keeps retries safe: if an earlier attempt to deliver to that subscription failed (a timeout, say), it may have applied the message before failing, so the retry answers `200` with `delivered: true` rather than starting anything.

The endpoint is rate-limited under the `workflow-mutation` policy.

## End-to-end curl example

This walks the full lifecycle of the [`message-catch.bpmn`](/fleans/samples/message-catch.bpmn) sample against the [Quick Start](/fleans/guides/quick-start/) Compose bundle (REST API on `http://localhost:8081`) — deploy the BPMN, start an instance (which sets `requestId = "req-456"` via a script task), then deliver the matching message:

<figure class="arch-diagram" style="--arch-ratio: 480 / 738; max-width: 520px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/message-correlation.html" src="/fleans/diagrams/message-correlation.html?embed=1" title="Message correlation sequence" loading="lazy"></iframe>
  <figcaption>The waiting instance registers <code>approvalReceived/req-456</code> and resumes when <code>POST /Execution/message</code> supplies the matching key. <a href="/fleans/diagrams/message-correlation.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

```bash
# 1. Download the sample and deploy it (jq JSON-escapes the XML).
curl -fsSLO https://nightbaker.github.io/fleans/samples/message-catch.bpmn
curl -X POST http://localhost:8081/Definitions/deploy \
  -H "Content-Type: application/json" \
  -d "{\"BpmnXml\": $(jq -Rs . < message-catch.bpmn)}"
# → {"processDefinitionKey":"message-catch-test","version":1,...}

# 2. Start an instance. The workflow's first script task sets _context.requestId = "req-456".
curl -X POST http://localhost:8081/Execution/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"message-catch-test"}'
# → {"workflowInstanceId":"..."}
# At this point the workflow has reached <intermediateCatchEvent id="waitApproval">
# and registered subscription "approvalReceived/req-456".

# 3. Deliver the message. CorrelationKey must equal the runtime value of requestId.
curl -X POST http://localhost:8081/Execution/message \
  -H "Content-Type: application/json" \
  -d '{"MessageName":"approvalReceived","CorrelationKey":"req-456","Variables":{"approvalDecision":"approved"}}'
# → {"delivered":true,"workflowInstanceIds":["..."]}
# The workflow resumes, "afterApproval" runs, and the instance reaches <endEvent>.
```

If you change `req-456` to `req-999` in the third request, the response is `404 Not Found` — no subscriber is waiting on `approvalReceived/req-999`.

If authentication is enabled, add an `Authorization: Bearer …` header to each call (see [Authentication](/fleans/reference/authentication/)). Contributors running the Aspire dev stack from source use its own ports instead of `8081`.

## Cookbook

### Pattern 1 — Request/response correlation (intermediate catch)

A workflow kicks off some external work, then waits for the result keyed by a per-instance request id — the caller passes a unique `requestId` in the `POST /Execution/start` `Variables`.

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<message id="responseMsg" name="serviceResponse">
  <extensionElements>
    <fleans:subscription correlationKey="= requestId" />
  </extensionElements>
</message>
<process id="rpc-pattern" isExecutable="true">
  <startEvent id="start" />
  <!-- requestId arrives with POST /Execution/start: {"WorkflowId":"rpc-pattern","Variables":{"requestId":"<unique id>"}} -->
  <serviceTask id="callExternal" type="rest-call">
    <!-- REST Caller plugin (github.com/nightBaker/fleans-plugins); include _context.requestId in the request body -->
  </serviceTask>
  <intermediateCatchEvent id="awaitResponse">
    <messageEventDefinition messageRef="responseMsg" />
  </intermediateCatchEvent>
  <endEvent id="end" />
  <sequenceFlow id="f1" sourceRef="start" targetRef="callExternal" />
  <sequenceFlow id="f2" sourceRef="callExternal" targetRef="awaitResponse" />
  <sequenceFlow id="f3" sourceRef="awaitResponse" targetRef="end" />
</process>
```

Each instance carries its own `requestId` and subscribes to the unique key — N concurrent instances do not collide.

### Pattern 2 — Event-driven start (message start event)

The process is created **by** the message, not waiting for one. Each unique correlation value spawns a fresh instance.

```xml
<message id="orderEvent" name="orderPlaced" />
<process id="order-handler" isExecutable="true">
  <startEvent id="start">
    <messageEventDefinition messageRef="orderEvent" />
  </startEvent>
  <!-- … -->
</process>
```

`POST /Execution/message` with `MessageName="orderPlaced"` and the order payload (including its id) in `Variables` creates a new instance and seeds its variables. Every request starts one, so send an `IdempotencyKey` (an event id, for example) when the sender may retry. Note: message start events deliberately have **no** `<extensionElements>` block — the correlation value comes from the API request, not from existing workflow state. The [`order-events.bpmn`](/fleans/samples/order-events.bpmn) sample is a complete process that starts on a message and then waits on a second, keyed message; the [Events Page](/fleans/guides/events-page/#best-practice-example) guide walks through it.

### Pattern 3 — Multi-step orchestration (multiple keyed catches)

A long-running workflow correlates against more than one message at different points. Each correlation value is set by a script task before the relevant catch.

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<message id="paymentMsg" name="paymentReceived">
  <extensionElements><fleans:subscription correlationKey="= orderId" /></extensionElements>
</message>
<message id="shipMsg" name="shipmentDispatched">
  <extensionElements><fleans:subscription correlationKey="= shipmentId" /></extensionElements>
</message>
<process id="order-saga" isExecutable="true">
  <!-- seed orderId on /start, await paymentReceived, allocate shipmentId, await shipmentDispatched -->
</process>
```

Each catch resolves its key against the variable scope at the moment it is reached, so the second catch can correlate on a value computed only after the first one completed. Pair this with [Variables and Scope](/fleans/guides/variables-and-scope/) to reason about which scope the variable lives in when the subscription registers.

## Common pitfalls

:::caution[Forgetting the `=` prefix]
Writing `correlationKey="requestId"` (no `=` prefix) happens to work today because the parser strips leading `=` and space characters — the resolved variable name is the same. **However**, idiomatic BPMN matches Zeebe conventions and editor tooling expects the `= ` form, so always write `correlationKey="= requestId"`. Treat the `=`-less form as accidentally compatible, not officially supported.
:::

:::caution[Compound expressions like `= a + b` are NOT supported]
The engine performs a **plain variable lookup**, not expression evaluation. `correlationKey="= a + b"` resolves the variable named `"a + b"` (which doesn't exist), and the workflow aborts with `InvalidOperationException: Correlation variable 'a + b' is null …`. Pre-compute the combined value in a script task and reference the resulting single variable instead:

```xml
<scriptTask id="computeKey" scriptFormat="csharp">
  <script>_context.compositeKey = _context.a + _context.b</script>
</scriptTask>
<!-- ... then ... -->
<message id="m" name="something">
  <extensionElements><fleans:subscription correlationKey="= compositeKey" /></extensionElements>
</message>
```
:::

:::caution[Variable not in scope at subscription time]
If the correlation variable is null (or absent) when the message-catch is reached, the workflow fails fast with `InvalidOperationException`. There is no fallback. Either pass the variable as part of the `POST /Execution/start` `Variables` payload, or set it via a script task that runs **before** the catch.
:::

:::caution[`MessageName` is case-sensitive]
The grain layer keys subscriptions on `{messageName}/{Uri.EscapeDataString(correlationKey)}`, and Orleans grain keys are case-sensitive. `approvalReceived` and `ApprovalReceived` are different messages. Mismatched casing returns 404 with no further hint.
:::

:::caution[Wrong `<extensionElements>` placement is silently ignored]
This bears repeating because it is the most expensive failure mode. `<extensionElements>` MUST be a direct child of `<bpmn:message>`. If you put it under `<intermediateCatchEvent>`, `<startEvent>`, or anywhere else, the parser doesn't see it — there is no parse error, no warning, the workflow deploys cleanly, and the `POST /Execution/message` simply never matches. Always compare with the [`message-catch.bpmn`](/fleans/samples/message-catch.bpmn) sample if a message subscription mysteriously fails to fire.
:::

## Limitations

A workflow can only have **one** active subscription per `(messageName, correlationKey)` pair. A second instance trying to subscribe on a key that is already taken fails with `Duplicate subscription`. Design correlation values to be per-instance unique (use a generated `Guid`, an order id, a session id) — never a shared business constant.

## See also

- [Variables and Scope](/fleans/guides/variables-and-scope/) — how variable scopes are organised and which scope a correlation lookup walks.
- [Error Handling](/fleans/guides/error-handling/) — recovering from failures inside the workflow that is awaiting a message.
- [BPMN Support](/fleans/concepts/bpmn-support/) — the canonical reference for parser behaviour and supported elements.
