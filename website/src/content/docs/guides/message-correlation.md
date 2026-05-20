---
title: Message Correlation
description: How Fleans correlates incoming BPMN messages to running workflow instances — BPMN definition, variable resolution semantics, the message API, and a curl-driven cookbook.
---

<!-- DRIFT-GUARD: cited line numbers verified at branch SHA b7d80af
     - src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs:2778-2790 (ResolveCorrelationKey: strips "= " prefix, plain GetVariable lookup, throws InvalidOperationException on null)
     - src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs:989-1011 (ProcessRegisterMessage: identical resolution logic for register-message commands)
     - src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs:895-925 (zeebe:subscription / fleans:subscription parse, TrimStart('=', ' '))
     - src/Fleans/Fleans.Api/Controllers/WorkflowController.cs:50-65 (SendMessage POST /Workflow/message)
     - src/Fleans/Fleans.ServiceDefaults/DTOs/SendMessageRequest.cs:5 (record SendMessageRequest(MessageName, CorrelationKey, Variables))
     - tests/manual/09-message-events/message-catch.bpmn (canonical intermediate-catch fixture)
     - tests/manual/16-message-start-event/message-start-event.bpmn (message start event fixture; deliberately no <extensionElements>)
     - tests/manual/21-event-subprocess-message/message-event-subprocess.bpmn (event sub-process fixture)
     If any of these line ranges shift, re-run the audit and update both the
     guide and this comment. -->

A **message correlation key** is the runtime value that routes an incoming message to the right workflow instance. Without it, every workflow waiting on `approvalReceived` would wake up — with it, only the one whose `requestId == "req-456"` does. This guide covers what a correlation key is in BPMN, how Fleans parses it, how the engine resolves it at runtime, the `POST /Workflow/message` API, and a small cookbook of the three patterns you'll most often need.

## What is a correlation key?

In BPMN, a **message** is a typed payload that flows between processes. When a workflow contains a `<intermediateCatchEvent>` or an event sub-process triggered by `<messageEventDefinition>`, that workflow *subscribes* to a (message-name, correlation-key) pair and pauses until something delivers a matching one.

The correlation **name** is static and lives on the `<bpmn:message>` definition. The correlation **key value** is dynamic — it is read from the workflow's variables at the moment the subscription is registered. So a workflow with `requestId = "req-456"` waiting on the message `approvalReceived` registers a subscription under the key `approvalReceived/req-456`. A `POST /Workflow/message` with `MessageName="approvalReceived"`, `CorrelationKey="req-456"` matches that exact subscription and resumes the workflow.

The grain layer enforces a **single subscriber per (messageName, correlationKey) pair** — two instances cannot wait on the same key at the same time. This is what gives correlation its routing power: the key is the address.

## BPMN definition

The canonical XML shape is taken verbatim from `tests/manual/09-message-events/message-catch.bpmn`:

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
    <!-- sequence flows omitted -->
  </process>
</definitions>
```

Three load-bearing details:

1. **The `xmlns:fleans="https://fleans.io/schema/bpmn/1.0"` namespace declaration** must appear on `<definitions>`. Without it, `<fleans:subscription>` is parsed as an unknown element and silently ignored (no correlation key, no error). Files exported from Camunda's modeler may use `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` instead — that's also accepted via Fleans's back-compat probe order.
2. **The `=` prefix** is the Zeebe expression marker. Fleans strips a literal `"= "` (equals-sign + space) at parse time and treats the remainder as a single variable name. The engine does **not** evaluate arbitrary expressions — see [Variable resolution semantics](#variable-resolution-semantics) below.
3. **Placement of `<extensionElements>` is project-specific** — read the caution that follows.

:::caution[Place `<extensionElements>` inside `<bpmn:message>`, not inside the message-event element]
Fleans only walks `<extensionElements>` that are **direct children of `<bpmn:message>`** (parser at `BpmnConverter.cs:895-925`). Putting the `<fleans:subscription>` block under the `<intermediateCatchEvent>` or the `<startEvent>` is **silently ignored** — the engine treats the message as "no correlation key" and your `POST /Workflow/message` will never match.

This is the single most common authoring mistake. The pattern is captured as a canonical rule in `CLAUDE.md` under *BPMN Fixture Authoring Rules*. Always check fixture #09 / #21 before authoring a new message-event workflow.
:::

The same shape applies whether the consumer is an `<intermediateCatchEvent>` or an event-sub-process `<startEvent>`; only the consuming element changes. See `tests/manual/21-event-subprocess-message/message-event-subprocess.bpmn` for the sub-process variant.

## Variable resolution semantics

When a workflow reaches a message-catch (or registers an event-sub-process subscription), the engine calls `ResolveCorrelationKey` (`WorkflowExecution.cs:2778-2790`):

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
- **Twin logic for the register-message path.** The same parse-and-resolve sequence is used by `ProcessRegisterMessage` (`WorkflowExecution.cs:989-1011`) which handles register-message commands emitted when a scope opens that contains a message-event sub-process. If you change one path, change both.

For message **start** events, no correlation key lives on the BPMN definition (fixture #16 deliberately omits `<extensionElements>` on the `<message>`). Routing for start events is by message *name* alone — the API caller supplies the correlation value as part of the `POST /Workflow/message` request, and a fresh workflow instance is spawned with that key recorded against its own variables.

## API request shape

The endpoint is `POST /Workflow/message`. The DTO is `SendMessageRequest` from `Fleans.ServiceDefaults`:

```csharp
public record SendMessageRequest(string MessageName, string? CorrelationKey, ExpandoObject? Variables);
```

Field semantics:

- **`MessageName`** (required, **case-sensitive**) — must match the BPMN `<message name="...">` exactly. Capitalization mismatch returns 404.
- **`CorrelationKey`** (optional) — the runtime string the subscriber's correlation expression resolved to. May be omitted only for messages whose BPMN definition has no `<fleans:subscription>` (e.g. message start events with no key).
- **`Variables`** (optional) — extra variables to merge into the receiving workflow's scope before it resumes. Useful for delivering response payloads (e.g. an `approvalDecision` field).

Responses:

- **200 OK** with `SendMessageResponse(bool Delivered, IList<Guid> WorkflowInstanceIds)` — `WorkflowInstanceIds` lists the affected instances (one for an intermediate catch, possibly multiple for start-event correlations).
- **400 Bad Request** if `MessageName` is null or whitespace.
- **404 Not Found** with `ErrorResponse("No subscription or start event found for message '...'")` if no subscriber is waiting on that `(name, key)` pair.

The endpoint is rate-limited under the `workflow-mutation` policy.

## End-to-end curl example

This walks the full lifecycle of fixture #09 — deploy the BPMN, start an instance (which sets `requestId = "req-456"` via a script task), then deliver the matching message:

```bash
# 1. Deploy the workflow.
curl -k -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d '{"BpmnXml":"<paste contents of tests/manual/09-message-events/message-catch.bpmn here>"}'
# → { "ProcessDefinitionKey": "...", "Version": 1 }

# 2. Start an instance. The workflow's first script task sets _context.requestId = "req-456".
curl -k -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"message-catch-test"}'
# → { "InstanceId": "..." }
# At this point the workflow has reached <intermediateCatchEvent id="waitApproval">
# and registered subscription "approvalReceived/req-456".

# 3. Deliver the message. CorrelationKey must equal the runtime value of requestId.
curl -k -X POST https://localhost:7140/Workflow/message \
  -H "Content-Type: application/json" \
  -d '{"MessageName":"approvalReceived","CorrelationKey":"req-456","Variables":{"approvalDecision":"approved"}}'
# → { "Delivered": true, "WorkflowInstanceIds": ["..."] }
# The workflow resumes, "afterApproval" runs, and the instance reaches <endEvent>.
```

If you change `req-456` to `req-999` in the third request, the response is `404 Not Found` — no subscriber is waiting on `approvalReceived/req-999`.

## Cookbook

### Pattern 1 — Request/response correlation (intermediate catch)

A workflow kicks off some external work, then waits for the result keyed by a per-instance request id. This is fixture #09's pattern.

```xml
<!-- Requires xmlns:fleans="https://fleans.io/schema/bpmn/1.0" on <bpmn:definitions> -->
<message id="responseMsg" name="serviceResponse">
  <extensionElements>
    <fleans:subscription correlationKey="= requestId" />
  </extensionElements>
</message>
<process id="rpc-pattern" isExecutable="true">
  <scriptTask id="generateId" scriptFormat="csharp">
    <script>_context.requestId = System.Guid.NewGuid().ToString()</script>
  </scriptTask>
  <serviceTask id="callExternal" type="rest-call">
    <!-- include _context.requestId in the request body -->
  </serviceTask>
  <intermediateCatchEvent id="awaitResponse">
    <messageEventDefinition messageRef="responseMsg" />
  </intermediateCatchEvent>
</process>
```

Each instance generates its own `requestId` and subscribes to the unique key — N concurrent instances do not collide.

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

`POST /Workflow/message` with `MessageName="orderPlaced"`, `CorrelationKey="<orderId>"`, and the order payload in `Variables` creates a new instance and seeds its variables. Note: message start events deliberately have **no** `<extensionElements>` block — the correlation value comes from the API request, not from existing workflow state. See `tests/manual/16-message-start-event/message-start-event.bpmn` for the full fixture.

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
Writing `correlationKey="requestId"` (no `=` prefix) happens to work today because the parser strips a leading `=` and space — the resolved variable name is the same. **However**, idiomatic BPMN matches Zeebe conventions and editor tooling expects the `= ` form, so always write `correlationKey="= requestId"`. Treat the `=`-less form as accidentally compatible, not officially supported.
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
If the correlation variable is null (or absent) when the message-catch is reached, the workflow fails fast with `InvalidOperationException`. There is no fallback. Either pass the variable as part of the `POST /Workflow/start` `Variables` payload, or set it via a script task that runs **before** the catch.
:::

:::caution[`MessageName` is case-sensitive]
The grain layer keys subscriptions on `{messageName}/{Uri.EscapeDataString(correlationKey)}`, and Orleans grain keys are case-sensitive. `approvalReceived` and `ApprovalReceived` are different messages. Mismatched casing returns 404 with no further hint.
:::

:::caution[Wrong `<extensionElements>` placement is silently ignored]
This bears repeating because it is the most expensive failure mode. `<extensionElements>` MUST be a direct child of `<bpmn:message>`. If you put it under `<intermediateCatchEvent>`, `<startEvent>`, or anywhere else, the parser doesn't see it — there is no parse error, no warning, the workflow deploys cleanly, and the `POST /Workflow/message` simply never matches. Always validate against `tests/manual/09-message-events/message-catch.bpmn` if a message subscription mysteriously fails to fire.
:::

## Limitations

:::caution[Boundary message events on `IntermediateCatchEvent` do not register]
Per regression test #9 (and the matching KNOWN BUG note in `tests/manual/09-message-events/test-plan.md`), boundary events attached to an `IntermediateCatchEvent` host do not register their subscriptions. This is the same root cause as regression test #8 (timer boundaries on intermediate catches) and affects message and signal boundaries the same way. As a workaround, attach the boundary to a host activity that the engine handles correctly (e.g. `userTask`, `serviceTask`, `subProcess`).
:::

A workflow can only have **one** active subscription per `(messageName, correlationKey)` pair. A second instance trying to subscribe on a key that is already taken fails with `Duplicate subscription`. Design correlation values to be per-instance unique (use a generated `Guid`, an order id, a session id) — never a shared business constant.

## See also

- [Variables and Scope](/fleans/guides/variables-and-scope/) — how variable scopes are organised and which scope a correlation lookup walks.
- [Error Handling](/fleans/guides/error-handling/) — recovering from failures inside the workflow that is awaiting a message.
- [BPMN Support](/fleans/concepts/bpmn-support/) — the canonical reference for parser behaviour and supported elements.
