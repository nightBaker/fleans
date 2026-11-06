# 31 — Events Page (Admin UI)

Verifies the new `/events` admin page shows registered start events and active message/signal subscriptions, that the **Refresh** button re-queries without a full reload, and that the delete-on-completion semantic of subscription tables surfaces in the UI (rows disappear after a Refresh once the subscribing workflow advances past the catch).

> **Automation:** steps 1, 3, 4, 5, 6, 8 and 9 are automated in `src/Fleans/Fleans.E2E.Tests/Specs/EventsPageTests.cs` (page object `PageObjects/EventsPage.cs`), using per-run unique message / signal names instead of the fixtures below (each spec searches for its own suffix). Steps 2 (empty state — needs an unshared clean DB), 7 (OIDC) and 10 (paging past 20 rows) remain manual.

## Prerequisites

- Aspire stack running: `dotnet run --project Fleans.Aspire` (from `src/Fleans/`).
- Clean dev DB (delete the SQLite file or set a fresh `FLEANS_SQLITE_CONNECTION`).
- Web UI reachable at `https://localhost:7124`.
- API origin: `https://localhost:7140`.
- BPMN fixtures available in this repo:
  - `tests/manual/16-message-start-event/message-start.bpmn`
  - `tests/manual/17-signal-start-event/signal-start.bpmn`
  - `tests/manual/09-message-events/message-catch.bpmn`

## Steps

### 1. Sidebar entry

- [ ] Navigate to `https://localhost:7124`. The left navigation app-bar shows an **Events** entry between **Custom Tasks** and the user-account section, with a flash icon.
- [ ] Click **Events**. The browser URL becomes `/events` and the page header reads **Registered Events**.

### 2. Empty state

Against a clean database:

- [ ] Each of the five sections renders its grid with the placeholder text "No … events registered." / "No active … subscriptions." in place of rows.
- [ ] The **Refresh** button is present and not disabled.

### 3. Message Start Event registration shows up

- [ ] Deploy `tests/manual/16-message-start-event/message-start.bpmn` via the Editor or `POST /Definitions/deploy`.
- [ ] Click **Refresh** on `/events`.
- [ ] **Message Start Events** section now lists one row: `MessageName=order-placed`, `ProcessDefinitionKey=order-process` (or whatever the fixture declares — match against the fixture).
- [ ] No full-page reload occurred (the URL bar did not flicker; only the sections re-rendered).

### 4. Signal Start Event registration shows up

- [ ] Deploy `tests/manual/17-signal-start-event/signal-start.bpmn`.
- [ ] Refresh `/events`.
- [ ] **Signal Start Events** section lists one row matching the fixture's `<bpmn:signalEventDefinition signalRef=…>` name and the process definition key.

### 5. Active Message Subscription appears, then disappears after delivery

- [ ] Deploy `tests/manual/09-message-events/message-catch.bpmn`.
- [ ] Start an instance via `POST /Execution/start` with the fixture's required initial variables (e.g. `requestId`).
- [ ] Refresh `/events`. **Active Message Subscriptions** lists one row:
    - `MessageName` matches the catch-event message
    - `CorrelationKey` matches the value derived from the start variables
    - `WorkflowInstanceId` is shown truncated to 8 chars + "…" — hovering reveals the full GUID via tooltip
    - `ActivityId` is the catch event's id
    - `ActivityInstanceId` is shown truncated, full on hover
- [ ] Send the message via `POST /Execution/message` with the matching `MessageName` and `CorrelationKey`.
- [ ] Refresh `/events`. The row no longer appears in **Active Message Subscriptions**. (This proves the delete-on-completion semantic of `MessageSubscriptions` surfaces in the UI without any row-level filter — if the row vanished, the engine already removed it.)

### 6. Refresh button re-queries without page reload

- [ ] Click **Refresh** while the page is already populated. The button shows its loading state briefly. The page does not navigate (no URL change, no scroll reset).

### 7. Authentication

- [ ] With no `Authentication` config on `Fleans.Api` and `Fleans.Web`, navigate to `/events` anonymously — the page loads.
- [ ] Configure JWT bearer auth on the Web app (per `tests/manual/30-web-auth/test-plan.md`). Navigate to `/events` while signed-out. Browser is redirected to the IdP. After signing in, lands on `/events` with the registered events visible. (No per-page `<AuthorizeView>` was added — the route-level wrapper covers it.)

### 8. Conditional Start Events filter

- [ ] Deploy a workflow with a `<bpmn:conditionalEventDefinition>` start event (e.g. fixture from `tests/manual/24-conditional-event/`).
- [ ] Refresh `/events`. The **Conditional Start Events** section lists the conditional listener with its `ConditionExpression`.
- [ ] Disable the process definition (via `POST /Definitions/disable` or the UI). Refresh.
- [ ] The conditional listener row disappears (the listener row is updated to `IsRegistered=false` and the page filters those out).

### 9. Search filters every section

- [ ] Type part of a message / signal name or process definition key into **Search by name or process key** and press Enter.
- [ ] Every section lists only rows whose name or process key contains the text (case-sensitive; Conditional Start Events match on process key). Message subscriptions also match on the correlation key.
- [ ] Clear the search and press Enter — all rows are back.

### 10. Paging (#754)

- [ ] Deploy `tests/manual/09-message-events/message-catch.bpmn` and start 25 instances with distinct `requestId` values.
- [ ] Refresh `/events`. **Active Message Subscriptions** shows 20 rows and a paginator under the grid reporting 25 items.
- [ ] Go to the next page — it shows the remaining 5 rows. The other sections keep their own page.
- [ ] Run a search while on page 2 — every section goes back to page 1.

## Expected outcome

All checkboxes above pass. The page is **read-only** (no write operations triggered from it) and the five sections accurately project the live state, one page at a time, of the persistence-layer registration / subscription tables on every Refresh.
