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

1. Open the management UI — `http://localhost:8080` with the [Quick Start](/fleans/guides/quick-start/) Compose bundle (contributors running the Aspire dev stack use its own port).
2. Click the **Events** entry in the left navigation app-bar (between **Custom Tasks** and the user-account section).
3. The page renders five sections, each backed by a different persistence table:

<figure class="arch-diagram" style="--arch-ratio: 700 / 748; max-width: 740px; margin-inline: auto">
  <iframe data-arch-src="/fleans/diagrams/events-page-flow.html" src="/fleans/diagrams/events-page-flow.html?embed=1" title="Events page data flow" loading="lazy"></iframe>
  <figcaption>Each of the five Events page sections reads one persistence table: start-event registrations written by a deploy, and subscriptions held while an instance is parked on a catch event; Refresh re-queries all five. <a href="/fleans/diagrams/events-page-flow.html" target="_blank" rel="noopener">Open interactive diagram ↗</a></figcaption>
</figure>

| Section | Source | Rows you'll see |
|---|---|---|
| **Message Start Events** | `MessageStartEventRegistrations` | one per `(MessageName, ProcessDefinitionKey)` pair registered by a deploy |
| **Signal Start Events** | `SignalStartEventRegistrations` | one per `(SignalName, ProcessDefinitionKey)` pair |
| **Conditional Start Events** | `ConditionalStartEventListenerState` filtered to `IsRegistered=true` | one per active conditional listener — its `ProcessDefinitionKey`, `ActivityId`, and `ConditionExpression` |
| **Active Message Subscriptions** | `MessageSubscriptions` | every workflow instance currently parked on a message catch event |
| **Active Signal Subscriptions** | `SignalSubscriptions` | every workflow instance currently parked on a signal catch event |

Each section is paged — 20 rows per page, with its own paginator under the grid — so the page stays responsive when thousands of instances are parked on a message or signal. Rows are ordered by name. Type into **Search by name or process key** and press Enter to narrow every section to rows whose message / signal name or process definition key contains that text (case-sensitive; a message subscription also matches on its correlation key); a new search returns every section to its first page.

Click **Refresh** to re-query all five sections, keeping each one on its current page. The page does not auto-poll — you decide when to take a fresh snapshot. Workflow / activity instance IDs are truncated to 8 characters + ellipsis; hover the truncated value to see the full GUID via tooltip.

## Best-practice example

You deploy the [`order-events.bpmn`](/fleans/samples/order-events.bpmn) sample: it starts on the message `orderReceived` (a message start event, `<bpmn:startEvent>` with a `<bpmn:messageEventDefinition>`) and then waits on `paymentReceived`, correlated on the `orderId` variable. Then you want to confirm both "the engine knows about my start event" and "an instance does pause on the catch". Against the Compose bundle's REST API on `http://localhost:8081`:

```bash
# 1. Download and deploy the sample
curl -fsSLO https://nightbaker.github.io/fleans/samples/order-events.bpmn
curl -X POST http://localhost:8081/Definitions/deploy \
  -H "Content-Type: application/json" \
  -d "{\"BpmnXml\": $(jq -Rs . < order-events.bpmn)}"

# 2. Open /events and click Refresh — the row should appear in
#    "Message Start Events" with MessageName="orderReceived".

# 3. Send the start message; orderId seeds the new instance's variables
curl -X POST http://localhost:8081/Execution/message \
  -H "Content-Type: application/json" \
  -d '{"MessageName":"orderReceived","Variables":{"orderId":"order-1"}}'

# 4. The instance starts and parks on the "Payment received" catch.
#    Refresh /events — it shows up under "Active Message Subscriptions"
#    with MessageName="paymentReceived" and correlation key "order-1".

# 5. Send the catch message — the row disappears from /events on the next
#    Refresh. (Subscription tables are delete-on-completion; the engine
#    removes the row when the catch fires.)
curl -X POST http://localhost:8081/Execution/message \
  -H "Content-Type: application/json" \
  -d '{"MessageName":"paymentReceived","CorrelationKey":"order-1"}'
```

The "row vanishes after the catch fires" cycle is the easiest way to confirm correlation keys end-to-end without reading event logs.

## What it does not do

The Events page is **read-only** in v1. It does not let you cancel a subscription, force-deliver a message, or edit a registration — those actions belong on the workflow-instance detail view (and the lifecycle endpoints under `/Execution/*`). It also does not auto-poll; large pages are typically polled by an external monitoring stack rather than the browser, so a manual Refresh fits the operator's workflow without burning bandwidth.

## Authentication

When OIDC is not configured, the page is anonymous; when it is configured, every route of the management UI — including `/events` — requires a signed-in user, and `/events` additionally requires the **operator** role (or **admin**). See [Authentication](/fleans/reference/authentication/) and [Authorization](/fleans/reference/authorization/).
