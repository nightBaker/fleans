---
title: Events Page
description: Admin-UI view of every event the engine is listening for and every workflow currently parked on a message or signal.
---

The **Events** page in the management UI (`/events`) gives operators one-stop visibility into "what is the engine listening for right now?" Without it, the only way to confirm "did my deploy register the start events I expect?" or "is this instance actually parked on a message?" is to hand-query the persistence tables.

## Why this exists

Two recurring questions during a deployment or incident:

1. **Did the deploy register my start events?** A typo in `<bpmn:messageEventDefinition messageRef=…>` or a missing `<bpmn:signalEventDefinition>` causes the start event to silently not register, and the workflow appears "deployed" but never starts. The Events page surfaces the registration table directly.
2. **Is this workflow really parked on a message I think it's parked on?** When debugging a stuck instance, you need to see whether `MessageSubscriptions` actually contains a row for that catch event with the correlation key you expect. Hand-querying SQLite or Postgres works but is unergonomic for a quick look.

Both questions are answered without leaving the browser.

## How to use it

1. Open the management UI (`https://localhost:7124` for the local Aspire stack).
2. Click the **Events** entry in the left navigation app-bar (between **Custom Tasks** and the user-account section).
3. The page renders five sections, each backed by a different persistence table:

| Section | Source | Rows you'll see |
|---|---|---|
| **Message Start Events** | `MessageStartEventRegistrations` | one per `(MessageName, ProcessDefinitionKey)` pair registered by a deploy |
| **Signal Start Events** | `SignalStartEventRegistrations` | one per `(SignalName, ProcessDefinitionKey)` pair |
| **Conditional Start Events** | `ConditionalStartEventListenerState` filtered to `IsRegistered=true` | one per active conditional listener — its `ProcessDefinitionKey`, `ActivityId`, and `ConditionExpression` |
| **Active Message Subscriptions** | `MessageSubscriptions` | every workflow instance currently parked on a message catch event |
| **Active Signal Subscriptions** | `SignalSubscriptions` | every workflow instance currently parked on a signal catch event |

Click **Refresh** to re-query all five sections. The page does not auto-poll — you decide when to take a fresh snapshot. Workflow / activity instance IDs are truncated to 8 characters + ellipsis; hover the truncated value to see the full GUID via tooltip.

## Best-practice example

You deploy a workflow that opens with `<bpmn:messageStartEvent>` and listens for `order-placed`. Then you want to confirm both "the engine knows about my start event" and "an instance does pause on the catch":

```bash
# 1. Deploy the fixture
curl -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d "{\"BpmnXml\": $(cat tests/manual/16-message-start-event/message-start.bpmn | jq -Rs .)}"

# 2. Open /events and click Refresh — the row should appear in
#    "Message Start Events" with MessageName="order-placed".

# 3. Send the start message
curl -X POST https://localhost:7140/Workflow/message \
  -H "Content-Type: application/json" \
  -d '{"MessageName":"order-placed","CorrelationKey":"order-1","Variables":{}}'

# 4. The instance starts, runs through any user / service tasks, and parks
#    on the next catch event. Refresh /events — that catch shows up under
#    "Active Message Subscriptions".

# 5. Send the catch message — the row disappears from /events on the next
#    Refresh. (Subscription tables are delete-on-completion; the engine
#    removes the row when the catch fires.)
```

The "row vanishes after the catch fires" cycle is the easiest way to confirm correlation keys end-to-end without reading event logs.

## What it does not do

The Events page is **read-only** in v1. It does not let you cancel a subscription, force-deliver a message, or edit a registration — those actions belong on the workflow-instance detail view (and the lifecycle endpoints under `/Workflow/*`). It also does not auto-poll; large pages are typically polled by an external monitoring stack rather than the browser, so a manual Refresh fits the operator's workflow without burning bandwidth.

## Authentication

Authentication is handled at the route level (`<AuthorizeRouteView>` in `Routes.razor`) plus the `RequireAuthenticatedUser` fallback policy in `Program.cs`. When OIDC is unconfigured, the page is anonymous; when configured, every route — including `/events` — gates behind the IdP. There is no per-page `<AuthorizeView>` wrapper to maintain. See [API JWT Authentication](/fleans/reference/api/#authentication) for the configuration surface.
