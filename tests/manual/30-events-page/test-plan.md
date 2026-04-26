# Test Plan: Events Page in Admin UI

## Scenario
The Admin UI has a new **Events** page (`/events`) that shows all registered start event listeners and active event subscriptions. This gives operators visibility into what events the running engine is watching.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`)
- Web UI at `https://localhost:7124`
- API at `https://localhost:7140`

## Steps

### Step 1 — Navigate to Events page
1. Open `https://localhost:7124/events`
2. Verify the page loads without errors
3. Verify five sections appear:
   - Message Start Events
   - Signal Start Events
   - Conditional Start Events
   - Active Message Subscriptions
   - Active Signal Subscriptions
4. All sections should say "No … registered" (empty database)

### Step 2 — Deploy a process with message start event
1. Deploy `tests/manual/16-message-start-event/message-start-event.bpmn`
2. Navigate to `/events`
3. Click **Refresh**

- [ ] "Message Start Events" table shows the deployed message name and process definition key

### Step 3 — Deploy a process with signal start event
1. Deploy `tests/manual/17-signal-start-event/signal-start-event.bpmn`
2. Click **Refresh** on the Events page

- [ ] "Signal Start Events" table shows the signal name and process definition key

### Step 4 — Create an active message subscription
1. Start an instance of a process that waits at an intermediate message catch event (e.g. `tests/manual/09-message-events/message-events.bpmn`)
2. Click **Refresh** on the Events page

- [ ] "Active Message Subscriptions" table shows the waiting instance with truncated instance ID (hover shows full ID)

### Step 5 — Create an active signal subscription
1. Start an instance of a process waiting for a signal (e.g. `tests/manual/10-signal-events/signal-events.bpmn`)
2. Click **Refresh**

- [ ] "Active Signal Subscriptions" table shows the waiting instance

### Step 6 — Verify Refresh button
1. Start another workflow instance that waits for an event
2. Click **Refresh** on the Events page (without reloading the browser)

- [ ] New subscription appears in the table

### Step 7 — Verify nav entry
1. Confirm the sidebar shows an "Events" item with a flash/lightning icon
2. Clicking it navigates to `/events`

## Expected Outcomes
- [ ] All five sections render without error
- [ ] Start event registrations reflect deployed process definitions
- [ ] Active subscriptions update when Refresh is clicked
- [ ] Truncated instance IDs with tooltip showing full ID
- [ ] "Events" nav item visible in sidebar
