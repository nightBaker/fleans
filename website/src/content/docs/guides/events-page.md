---
title: Events Page
description: The Admin UI Events page shows all registered start-event listeners and active intermediate-event subscriptions at a glance.
---

The **Events** page (`/events` in the Admin UI) gives you a live view of every event
subscription the engine currently holds. This is useful for debugging "why didn't my
message/signal arrive?" scenarios, confirming that a newly deployed process registered
its start events, and monitoring how many workflows are parked waiting for external input.

## Why a dedicated events page?

Workflow engines route external triggers (messages, signals, timers, conditions) through
a registry of named listeners. When something isn't working — a message never fires a
new instance, a signal never unblocks a waiting workflow — the first question is: *is
the subscription even registered?* Previously you had to query the database directly.
The Events page surfaces that state in one place without SQL.

## How to use it

Navigate to **Events** in the Admin UI sidebar. The page loads automatically and shows
five sections:

| Section | What it contains |
|---|---|
| **Message Start Events** | Message names and the process key they will start when received |
| **Signal Start Events** | Signal names and the process key they will start when broadcast |
| **Conditional Start Events** | Process key, activity ID, and condition expression for conditional start listeners |
| **Active Message Subscriptions** | Currently waiting `<intermediateCatchEvent>` (message) — shows message name, correlation key, instance ID, and activity ID |
| **Active Signal Subscriptions** | Currently waiting `<intermediateCatchEvent>` (signal) — shows signal name, instance ID, and activity ID |

Click **Refresh** to re-query without a full page reload.

## Example: debugging a stalled message flow

1. A workflow instance should have been unblocked by a `POST /Workflow/message` call,
   but it's still running.
2. Open `/events` and look at **Active Message Subscriptions**.
3. If no row appears for your message name, the workflow never reached the catch event —
   check the active activities via the Instances view instead.
4. If a row does appear, the correlation key in the subscription must match the one in
   your API call exactly (case-sensitive).

## Example: verifying a message start event after deploy

After deploying a process that starts on a message:

1. Navigate to `/events`.
2. The **Message Start Events** section should list your message name and process key.
3. If it's missing, re-deploy the process definition (message start event registration
   happens automatically on deploy when the process is enabled).

## Best practices

- Use the Events page as a first step in any message/signal debugging session — it's
  faster than querying the database.
- After undeploying (disabling) a process, the corresponding start-event rows should
  disappear from the page. If they linger, there may be a registration cleanup issue.
- Intermediate subscriptions (`Active Message Subscriptions`, `Active Signal Subscriptions`)
  are transient — they appear when a workflow parks at a catch event and vanish as soon
  as the event is delivered.
