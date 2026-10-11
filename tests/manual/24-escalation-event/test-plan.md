# 20 — Escalation Event

## Scenario
Tests BPMN Escalation Events: escalation end event (child terminates and throws escalation to parent), escalation intermediate throw event (child continues and throws escalation), interrupting and non-interrupting escalation boundary events, and uncaught escalation (no boundary handler).

## Prerequisites
- Aspire stack running
- Deploy child processes FIRST before parent processes

## Fixtures
1. `child-escalation-end.bpmn` — child process that throws an escalation via end event
2. `child-escalation-throw.bpmn` — child process that throws an escalation mid-flow and continues
3. `parent-escalation-interrupting.bpmn` — parent with interrupting escalation boundary on CallActivity
4. `parent-escalation-non-interrupting.bpmn` — parent with non-interrupting escalation boundary on CallActivity
5. `escalation-event-subprocess.bpmn` — escalation thrown inside an embedded sub-process, caught by a process-level non-interrupting escalation event sub-process (Test D)

---

## Test A: Escalation End Event with Interrupting Boundary

### Steps
1. Deploy `child-escalation-end.bpmn`
2. Deploy `parent-escalation-interrupting.bpmn`
3. Start `parent-escalation-interrupting`, open Instance Viewer

### Expected Outcome
- [ ] Parent instance status: **Completed**
- [ ] `escalationHandler` in completed activities (boundary event caught escalation)
- [ ] `happyEnd` NOT in completed activities (call activity was interrupted)
- [ ] `escalationEnd` in completed activities
- [ ] Child instance status: **Completed** (EscalationEndEvent terminates the child)

---

## Test B: Escalation Intermediate Throw with Non-Interrupting Boundary

### Steps
1. Deploy `child-escalation-throw.bpmn`
2. Deploy `parent-escalation-non-interrupting.bpmn`
3. Start `parent-escalation-non-interrupting`, open Instance Viewer

### Expected Outcome
- [ ] Parent instance status: **Completed**
- [ ] `escalationHandler` in completed activities (non-interrupting boundary fired)
- [ ] `happyEnd` in completed activities (call activity was NOT interrupted — child completed normally)
- [ ] `escalationEnd` in completed activities
- [ ] Child instance completed normally (continued after throw)

---

## Test C: Uncaught Escalation (no boundary handler)

### Steps
1. Deploy `child-escalation-end.bpmn` (if not already deployed)
2. Create a simple parent with a CallActivity to `child-escalation-end` but NO escalation boundary
3. Start the parent, open Instance Viewer

### Expected Outcome
- [ ] Parent instance status: **Completed** (escalation is non-faulting per BPMN spec)
- [ ] No error raised — uncaught escalation is silently recorded
- [ ] Check structured logs for `EscalationUncaughtRaised` log entry

---

## Test D: Escalation caught by an Event Sub-Process

> **KNOWN BUG:** escalation-triggered event sub-processes are not supported — the escalation start event is parsed as a plain start event and the escalation is treated as uncaught. See [#783](https://github.com/nightBaker/fleans/issues/783). Spec `EscalationEventSubProcessTests` is `[Ignore]`d.

### Steps
1. Deploy `escalation-event-subprocess.bpmn`; start `escalation-event-subprocess`

### Expected Outcome
- [ ] `escalationHandler` completed (event sub-process fired on `ESC_LOW_STOCK`)
- [ ] Main flow continues: `afterThrow`, `work`, `end` completed (non-interrupting)
- [ ] Variables: `escalationHandled` = **true**, `mainContinued` = **true**
