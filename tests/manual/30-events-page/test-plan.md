# 30 — Events Page

**Feature:** #374 — Registered events management page.

**Scope:** The `/events` page in the Fleans admin UI shows all registered event
listeners and active subscriptions: message start events, signal start events,
conditional start events, active message subscriptions, and active signal
subscriptions.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Web UI reachable at `https://localhost:7140`.
- Clean dev DB (fresh state shows all sections as empty; populated state shows
  live data).

## Scenarios

### 1. Empty state

**Steps:**

1. Start the Aspire stack with a fresh SQLite DB.
2. Navigate to `https://localhost:7140/events`.
3. Verify the page loads without errors.

**Expected:**

- [ ] "Events" nav item is visible in the sidebar / app bar.
- [ ] Page title is "Registered Events".
- [ ] All five sections display "No ... registered/waiting" messages.
- [ ] No errors in the browser console.

---

### 2. Message start event — deploy and verify

**Steps:**

1. Deploy `tests/manual/09-message-events/message-events.bpmn` (or any process
   with a `<messageStartEvent>`).
2. Refresh or navigate to `/events`.

**Expected:**

- [ ] **Message Start Events** section lists the message name and process key.
- [ ] The row count matches the number of deployed message start events.

---

### 3. Signal start event — deploy and verify

**Steps:**

1. Deploy `tests/manual/10-signal-events/signal-events.bpmn` (or any process
   with a `<signalStartEvent>`).
2. Refresh or navigate to `/events`.

**Expected:**

- [ ] **Signal Start Events** section lists the signal name and process key.

---

### 4. Conditional start event — deploy and verify

**Steps:**

1. Deploy `tests/manual/24-conditional-event/conditional-event-test.bpmn`.
2. Refresh `/events`.

**Expected:**

- [ ] **Conditional Start Events** section lists the process key, activity ID,
  and condition expression.

---

### 5. Active message subscription — workflow waiting

**Steps:**

1. Start an instance of a workflow that has a `<intermediateCatchEvent>` with a
   message (e.g. `tests/manual/09-message-events`). The workflow parks at the
   catch event.
2. Refresh `/events`.

**Expected:**

- [ ] **Active Message Subscriptions** section shows the message name (parsed
  from the combined key), correlation key, the first 8 chars of the workflow
  instance ID (with full ID on hover), and activity ID.
- [ ] After sending the message via `POST /Workflow/message`, the row disappears
  on the next refresh.

---

### 6. Active signal subscription — workflow waiting

**Steps:**

1. Start an instance of a workflow with a signal intermediate catch event
   (e.g. `tests/manual/10-signal-events`).
2. Refresh `/events`.

**Expected:**

- [ ] **Active Signal Subscriptions** section shows the signal name, instance ID,
  and activity ID.
- [ ] After broadcasting the signal via `POST /Workflow/signal`, the row
  disappears on the next refresh.

---

### 7. Refresh button

**Steps:**

1. Open `/events` when a workflow is waiting for a message.
2. In another tab, send the message via API.
3. Click **Refresh** on the events page.

**Expected:**

- [ ] The Refresh button shows a loading spinner while fetching.
- [ ] After refresh, the subscription row is gone.
- [ ] No page reload required.
