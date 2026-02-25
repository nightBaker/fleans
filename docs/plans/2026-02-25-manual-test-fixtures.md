# Manual Test Fixtures Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create 12 manual test folders with BPMN fixture files and test-plan.md files covering all implemented BPMN features.

**Architecture:** Each folder is self-contained with `.bpmn` files and a `test-plan.md`. Tests are executed via Chrome (Web UI for deploy/start/verify) and curl (API for messages/signals). Design doc: `docs/plans/2026-02-25-manual-test-plan-design.md`.

**Tech Stack:** BPMN 2.0 XML, Blazor Web UI, REST API (curl), Aspire

---

### Task 1: Create folder structure

**Files:**
- Create: `tests/manual/` (directory)

**Step 1: Create all 12 feature directories**

```bash
mkdir -p tests/manual/{01-basic-workflow,02-script-tasks,03-exclusive-gateway,04-parallel-gateway,05-event-based-gateway,06-call-activity,07-subprocess,08-timer-events,09-message-events,10-signal-events,11-error-boundary,12-variable-scoping}
```

**Step 2: Commit**

```bash
git add tests/manual/
git commit -m "chore: create manual test folder structure"
```

---

### Task 2: 01-basic-workflow

**Files:**
- Create: `tests/manual/01-basic-workflow/simple-workflow.bpmn`
- Create: `tests/manual/01-basic-workflow/test-plan.md`

**Step 1: Create simple-workflow.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="simple-workflow">
    <startEvent id="start" />
    <task id="task1" />
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="task1" />
    <sequenceFlow id="f2" sourceRef="task1" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 01 — Basic Workflow

## Scenario
Deploy and run the simplest possible workflow: start → task → end. Verifies the core deploy/start/complete lifecycle works.

## Prerequisites
- Aspire stack running (`dotnet run --project Fleans.Aspire` from `src/Fleans/`)

## Steps

### 1. Deploy the workflow
- Open the Workflows page in the Web UI
- Click "Create New" to open the BPMN editor
- Import `simple-workflow.bpmn` via drag-drop
- Click Deploy, confirm the deployment dialog

### 2. Start an instance
- On the Workflows page, find `simple-workflow` and click "Start"
- Click "View Instances" to see the instance list
- Click "View" on the new instance to open the Instance Viewer

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Activities tab: 3 completed activities (start, task1, end)
- [ ] No failed activities
- [ ] No active activities remaining
- [ ] BPMN canvas highlights the completed path (start → task1 → end)
```

**Step 3: Commit**

```bash
git add tests/manual/01-basic-workflow/
git commit -m "test: add manual test for basic workflow (01)"
```

---

### Task 3: 02-script-tasks

**Files:**
- Create: `tests/manual/02-script-tasks/script-variable-manipulation.bpmn`
- Create: `tests/manual/02-script-tasks/test-plan.md`

**Step 1: Create script-variable-manipulation.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="script-variables">
    <startEvent id="start" />
    <scriptTask id="setVar" scriptFormat="csharp">
      <script>_context.x = 10</script>
    </scriptTask>
    <scriptTask id="incrementVar" scriptFormat="csharp">
      <script>_context.x = _context.x + 5</script>
    </scriptTask>
    <scriptTask id="createSecondVar" scriptFormat="csharp">
      <script>_context.greeting = "hello"</script>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setVar" />
    <sequenceFlow id="f2" sourceRef="setVar" targetRef="incrementVar" />
    <sequenceFlow id="f3" sourceRef="incrementVar" targetRef="createSecondVar" />
    <sequenceFlow id="f4" sourceRef="createSecondVar" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 02 — Script Tasks

## Scenario
Execute a chain of script tasks that create and mutate workflow variables. Verifies the script engine evaluates C# expressions and persists variables correctly.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy the workflow
- Open the Workflows page
- Click "Create New", import `script-variable-manipulation.bpmn`
- Click Deploy, confirm

### 2. Start an instance
- Click "Start" on `script-variables`
- Navigate to the Instance Viewer

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Activities tab: 5 completed activities (start, setVar, incrementVar, createSecondVar, end)
- [ ] Variables tab: `x` = **15** (set to 10, then incremented by 5)
- [ ] Variables tab: `greeting` = **"hello"**
- [ ] No failed activities
```

**Step 3: Commit**

```bash
git add tests/manual/02-script-tasks/
git commit -m "test: add manual test for script tasks (02)"
```

---

### Task 4: 03-exclusive-gateway

**Files:**
- Create: `tests/manual/03-exclusive-gateway/conditional-branching.bpmn`
- Create: `tests/manual/03-exclusive-gateway/test-plan.md`

**Step 1: Create conditional-branching.bpmn**

Note: The script sets `x = 7`, so the condition `${x > 5}` is true and the workflow takes the "high" path. The default path goes to "lowEnd".

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="exclusive-gateway-test">
    <startEvent id="start" />
    <scriptTask id="setX" scriptFormat="csharp">
      <script>_context.x = 7</script>
    </scriptTask>
    <exclusiveGateway id="gateway" default="defaultFlow" />
    <task id="highTask" />
    <task id="lowTask" />
    <endEvent id="highEnd" />
    <endEvent id="lowEnd" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setX" />
    <sequenceFlow id="f2" sourceRef="setX" targetRef="gateway" />
    <sequenceFlow id="conditionalFlow" sourceRef="gateway" targetRef="highTask">
      <conditionExpression>${x &gt; 5}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id="defaultFlow" sourceRef="gateway" targetRef="lowTask" />
    <sequenceFlow id="f3" sourceRef="highTask" targetRef="highEnd" />
    <sequenceFlow id="f4" sourceRef="lowTask" targetRef="lowEnd" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 03 — Exclusive Gateway

## Scenario
Set a variable `x = 7` via script, then branch through an exclusive gateway with condition `x > 5`. The true path should be taken; the default path should not.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy the workflow
- Import `conditional-branching.bpmn` and deploy

### 2. Start an instance
- Start `exclusive-gateway-test`, open Instance Viewer

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, setX, gateway, **highTask**, highEnd
- [ ] `lowTask` and `lowEnd` do NOT appear in completed activities
- [ ] Conditions tab: `conditionalFlow` evaluated to **true**
- [ ] Conditions tab: `defaultFlow` was **not taken**
- [ ] Variables tab: `x` = **7**
- [ ] BPMN canvas highlights the start → setX → gateway → highTask → highEnd path
```

**Step 3: Commit**

```bash
git add tests/manual/03-exclusive-gateway/
git commit -m "test: add manual test for exclusive gateway (03)"
```

---

### Task 5: 04-parallel-gateway

**Files:**
- Create: `tests/manual/04-parallel-gateway/fork-join.bpmn`
- Create: `tests/manual/04-parallel-gateway/test-plan.md`

**Step 1: Create fork-join.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="parallel-gateway-test">
    <startEvent id="start" />
    <parallelGateway id="fork" />
    <scriptTask id="branchA" scriptFormat="csharp">
      <script>_context.a = "done"</script>
    </scriptTask>
    <scriptTask id="branchB" scriptFormat="csharp">
      <script>_context.b = "done"</script>
    </scriptTask>
    <parallelGateway id="join" />
    <task id="afterJoin" />
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
    <sequenceFlow id="f2" sourceRef="fork" targetRef="branchA" />
    <sequenceFlow id="f3" sourceRef="fork" targetRef="branchB" />
    <sequenceFlow id="f4" sourceRef="branchA" targetRef="join" />
    <sequenceFlow id="f5" sourceRef="branchB" targetRef="join" />
    <sequenceFlow id="f6" sourceRef="join" targetRef="afterJoin" />
    <sequenceFlow id="f7" sourceRef="afterJoin" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 04 — Parallel Gateway

## Scenario
Fork into two parallel branches (each sets a variable), join, then continue to end. Verifies parallel execution and synchronization at the join.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy and start
- Import `fork-join.bpmn`, deploy, start `parallel-gateway-test`

### 2. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Both `branchA` and `branchB` appear in completed activities
- [ ] `afterJoin` and `end` also completed (join waited for both branches)
- [ ] Variables tab shows separate variable scopes for each branch
- [ ] BPMN canvas highlights both parallel paths
```

**Step 3: Commit**

```bash
git add tests/manual/04-parallel-gateway/
git commit -m "test: add manual test for parallel gateway (04)"
```

---

### Task 6: 05-event-based-gateway

**Files:**
- Create: `tests/manual/05-event-based-gateway/timer-vs-message-race.bpmn`
- Create: `tests/manual/05-event-based-gateway/test-plan.md`

**Step 1: Create timer-vs-message-race.bpmn**

Timer is set to PT30S. We send the message before the timer fires, so the message path wins.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:zeebe="http://camunda.org/schema/zeebe/1.0">
  <message id="msg1" name="continueProcess">
    <extensionElements>
      <zeebe:subscription correlationKey="= orderId" />
    </extensionElements>
  </message>
  <process id="event-based-gateway-test">
    <startEvent id="start" />
    <scriptTask id="setCorrelation" scriptFormat="csharp">
      <script>_context.orderId = "order-123"</script>
    </scriptTask>
    <eventBasedGateway id="ebg" />
    <intermediateCatchEvent id="timerCatch">
      <timerEventDefinition>
        <timeDuration>PT30S</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <intermediateCatchEvent id="msgCatch">
      <messageEventDefinition messageRef="msg1" />
    </intermediateCatchEvent>
    <task id="timerPath" />
    <task id="msgPath" />
    <endEvent id="timerEnd" />
    <endEvent id="msgEnd" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setCorrelation" />
    <sequenceFlow id="f2" sourceRef="setCorrelation" targetRef="ebg" />
    <sequenceFlow id="f3" sourceRef="ebg" targetRef="timerCatch" />
    <sequenceFlow id="f4" sourceRef="ebg" targetRef="msgCatch" />
    <sequenceFlow id="f5" sourceRef="timerCatch" targetRef="timerPath" />
    <sequenceFlow id="f6" sourceRef="msgCatch" targetRef="msgPath" />
    <sequenceFlow id="f7" sourceRef="timerPath" targetRef="timerEnd" />
    <sequenceFlow id="f8" sourceRef="msgPath" targetRef="msgEnd" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 05 — Event-Based Gateway

## Scenario
An event-based gateway waits for either a timer (30s) or a message. We send the message via API before the timer fires, so the message path should win.

## Prerequisites
- Aspire stack running
- Note the API port from Aspire dashboard (e.g., `http://localhost:<port>`)

## Steps

### 1. Deploy and start
- Import `timer-vs-message-race.bpmn`, deploy, start `event-based-gateway-test`
- Open Instance Viewer — should show instance as **Running** with `ebg` active

### 2. Send message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "continueProcess", "correlationKey": "order-123", "variables": {}}'
```
Expected response: `{"delivered": true}`

### 3. Verify outcome (refresh Instance Viewer)
- [ ] Instance status: **Completed**
- [ ] `msgCatch` and `msgPath` appear in completed activities
- [ ] `timerCatch` and `timerPath` do **NOT** appear in completed activities
- [ ] BPMN canvas highlights the message path
- [ ] Variables tab: `orderId` = **"order-123"**
```

**Step 3: Commit**

```bash
git add tests/manual/05-event-based-gateway/
git commit -m "test: add manual test for event-based gateway (05)"
```

---

### Task 7: 06-call-activity

**Files:**
- Create: `tests/manual/06-call-activity/child-process.bpmn`
- Create: `tests/manual/06-call-activity/parent-process.bpmn`
- Create: `tests/manual/06-call-activity/test-plan.md`

**Step 1: Create child-process.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="child-process">
    <startEvent id="childStart" />
    <scriptTask id="childScript" scriptFormat="csharp">
      <script>_context.result = _context.input * 2</script>
    </scriptTask>
    <endEvent id="childEnd" />
    <sequenceFlow id="cf1" sourceRef="childStart" targetRef="childScript" />
    <sequenceFlow id="cf2" sourceRef="childScript" targetRef="childEnd" />
  </process>
</definitions>
```

**Step 2: Create parent-process.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="parent-process">
    <startEvent id="parentStart" />
    <scriptTask id="setInput" scriptFormat="csharp">
      <script>_context.input = 21</script>
    </scriptTask>
    <callActivity id="callChild" calledElement="child-process">
      <extensionElements>
        <inputMapping source="input" target="input" />
        <outputMapping source="result" target="result" />
      </extensionElements>
    </callActivity>
    <endEvent id="parentEnd" />
    <sequenceFlow id="pf1" sourceRef="parentStart" targetRef="setInput" />
    <sequenceFlow id="pf2" sourceRef="setInput" targetRef="callChild" />
    <sequenceFlow id="pf3" sourceRef="callChild" targetRef="parentEnd" />
  </process>
</definitions>
```

**Step 3: Create test-plan.md**

```markdown
# 06 — Call Activity

## Scenario
A parent process sets `input = 21`, calls a child process that computes `result = input * 2`, and maps the result back. Verifies cross-process variable mapping.

## Prerequisites
- Aspire stack running
- **Deploy `child-process.bpmn` FIRST** — the parent references it by `calledElement="child-process"`

## Steps

### 1. Deploy the child process
- Import `child-process.bpmn`, deploy

### 2. Deploy the parent process
- Import `parent-process.bpmn`, deploy

### 3. Start an instance of the parent
- Start `parent-process`, open Instance Viewer

### 4. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Parent completed activities: parentStart, setInput, callChild, parentEnd
- [ ] Variables tab (parent scope): `input` = **21**, `result` = **42**
- [ ] A child workflow instance was created and also completed
```

**Step 4: Commit**

```bash
git add tests/manual/06-call-activity/
git commit -m "test: add manual test for call activity (06)"
```

---

### Task 8: 07-subprocess

**Files:**
- Create: `tests/manual/07-subprocess/embedded-subprocess.bpmn`
- Create: `tests/manual/07-subprocess/test-plan.md`

**Step 1: Create embedded-subprocess.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="subprocess-test">
    <startEvent id="start" />
    <subProcess id="sub1">
      <startEvent id="subStart" />
      <scriptTask id="subScript" scriptFormat="csharp">
        <script>_context.subVar = "from-subprocess"</script>
      </scriptTask>
      <endEvent id="subEnd" />
      <sequenceFlow id="sf1" sourceRef="subStart" targetRef="subScript" />
      <sequenceFlow id="sf2" sourceRef="subScript" targetRef="subEnd" />
    </subProcess>
    <task id="afterSub" />
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="sub1" />
    <sequenceFlow id="f2" sourceRef="sub1" targetRef="afterSub" />
    <sequenceFlow id="f3" sourceRef="afterSub" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 07 — Embedded SubProcess

## Scenario
A workflow contains an embedded subprocess with its own start → script → end. The subprocess sets a variable, then the parent continues to a task and end.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy and start
- Import `embedded-subprocess.bpmn`, deploy, start `subprocess-test`

### 2. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Subprocess activities visible: subStart, subScript, subEnd
- [ ] Parent activities: start, sub1, afterSub, end
- [ ] Variables tab: `subVar` = **"from-subprocess"**
- [ ] BPMN canvas shows subprocess boundary with internal activities highlighted
```

**Step 3: Commit**

```bash
git add tests/manual/07-subprocess/
git commit -m "test: add manual test for embedded subprocess (07)"
```

---

### Task 9: 08-timer-events

**Files:**
- Create: `tests/manual/08-timer-events/timer-intermediate-catch.bpmn`
- Create: `tests/manual/08-timer-events/timer-boundary.bpmn`
- Create: `tests/manual/08-timer-events/test-plan.md`

**Step 1: Create timer-intermediate-catch.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="timer-catch-test">
    <startEvent id="start" />
    <intermediateCatchEvent id="waitTimer">
      <timerEventDefinition>
        <timeDuration>PT5S</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <scriptTask id="afterTimer" scriptFormat="csharp">
      <script>_context.timerFired = true</script>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="waitTimer" />
    <sequenceFlow id="f2" sourceRef="waitTimer" targetRef="afterTimer" />
    <sequenceFlow id="f3" sourceRef="afterTimer" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create timer-boundary.bpmn**

The boundary timer (PT5S) is attached to a task. Since TaskActivity completes immediately, the timer may not fire. To test boundary interruption, we use a message catch event as the host activity — it blocks until a message arrives, giving the timer time to fire.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <message id="msg1" name="neverArrives" />
  <process id="timer-boundary-test">
    <startEvent id="start" />
    <intermediateCatchEvent id="blockingWait">
      <messageEventDefinition messageRef="msg1" />
    </intermediateCatchEvent>
    <boundaryEvent id="boundaryTimer" attachedToRef="blockingWait">
      <timerEventDefinition>
        <timeDuration>PT5S</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <task id="normalEnd" />
    <scriptTask id="timeoutPath" scriptFormat="csharp">
      <script>_context.timedOut = true</script>
    </scriptTask>
    <endEvent id="end1" />
    <endEvent id="end2" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="blockingWait" />
    <sequenceFlow id="f2" sourceRef="blockingWait" targetRef="normalEnd" />
    <sequenceFlow id="f3" sourceRef="boundaryTimer" targetRef="timeoutPath" />
    <sequenceFlow id="f4" sourceRef="normalEnd" targetRef="end1" />
    <sequenceFlow id="f5" sourceRef="timeoutPath" targetRef="end2" />
  </process>
</definitions>
```

**Step 3: Create test-plan.md**

```markdown
# 08 — Timer Events

## Scenario A: Timer Intermediate Catch
A workflow pauses at a timer catch event (5s), then continues. Verifies timer scheduling and resumption.

## Scenario B: Timer Boundary Event
A blocking activity (message catch that never receives a message) has a 5s boundary timer. The timer fires and interrupts, taking the timeout path.

## Prerequisites
- Aspire stack running

## Steps — Scenario A (timer-intermediate-catch.bpmn)

### 1. Deploy and start
- Import `timer-intermediate-catch.bpmn`, deploy, start `timer-catch-test`

### 2. Observe waiting state
- Open Instance Viewer immediately — instance should be **Running**
- `waitTimer` should be an active activity

### 3. Wait ~5 seconds and refresh
- [ ] Instance status: **Completed**
- [ ] `waitTimer` and `afterTimer` in completed activities
- [ ] Variables tab: `timerFired` = **true**

## Steps — Scenario B (timer-boundary.bpmn)

### 1. Deploy and start
- Import `timer-boundary.bpmn`, deploy, start `timer-boundary-test`

### 2. Observe waiting state
- Instance should be **Running** with `blockingWait` active

### 3. Wait ~5 seconds and refresh (do NOT send the message)
- [ ] Instance status: **Completed**
- [ ] `timeoutPath` in completed activities (boundary timer fired)
- [ ] `normalEnd` NOT in completed activities (message path was interrupted)
- [ ] Variables tab: `timedOut` = **true**
```

**Step 4: Commit**

```bash
git add tests/manual/08-timer-events/
git commit -m "test: add manual test for timer events (08)"
```

---

### Task 10: 09-message-events

**Files:**
- Create: `tests/manual/09-message-events/message-catch.bpmn`
- Create: `tests/manual/09-message-events/message-boundary.bpmn`
- Create: `tests/manual/09-message-events/test-plan.md`

**Step 1: Create message-catch.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:zeebe="http://camunda.org/schema/zeebe/1.0">
  <message id="msg1" name="approvalReceived">
    <extensionElements>
      <zeebe:subscription correlationKey="= requestId" />
    </extensionElements>
  </message>
  <process id="message-catch-test">
    <startEvent id="start" />
    <scriptTask id="setCorrelation" scriptFormat="csharp">
      <script>_context.requestId = "req-456"</script>
    </scriptTask>
    <intermediateCatchEvent id="waitApproval">
      <messageEventDefinition messageRef="msg1" />
    </intermediateCatchEvent>
    <task id="afterApproval" />
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setCorrelation" />
    <sequenceFlow id="f2" sourceRef="setCorrelation" targetRef="waitApproval" />
    <sequenceFlow id="f3" sourceRef="waitApproval" targetRef="afterApproval" />
    <sequenceFlow id="f4" sourceRef="afterApproval" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create message-boundary.bpmn**

A long timer (PT60S) is the host activity. A boundary message event can interrupt it.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:zeebe="http://camunda.org/schema/zeebe/1.0">
  <message id="cancelMsg" name="cancelRequest">
    <extensionElements>
      <zeebe:subscription correlationKey="= requestId" />
    </extensionElements>
  </message>
  <process id="message-boundary-test">
    <startEvent id="start" />
    <scriptTask id="setCorrelation" scriptFormat="csharp">
      <script>_context.requestId = "req-789"</script>
    </scriptTask>
    <intermediateCatchEvent id="longWait">
      <timerEventDefinition>
        <timeDuration>PT60S</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <boundaryEvent id="cancelBoundary" attachedToRef="longWait">
      <messageEventDefinition messageRef="cancelMsg" />
    </boundaryEvent>
    <task id="normalPath" />
    <scriptTask id="cancelPath" scriptFormat="csharp">
      <script>_context.cancelled = true</script>
    </scriptTask>
    <endEvent id="end1" />
    <endEvent id="end2" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="setCorrelation" />
    <sequenceFlow id="f2" sourceRef="setCorrelation" targetRef="longWait" />
    <sequenceFlow id="f3" sourceRef="longWait" targetRef="normalPath" />
    <sequenceFlow id="f4" sourceRef="cancelBoundary" targetRef="cancelPath" />
    <sequenceFlow id="f5" sourceRef="normalPath" targetRef="end1" />
    <sequenceFlow id="f6" sourceRef="cancelPath" targetRef="end2" />
  </process>
</definitions>
```

**Step 3: Create test-plan.md**

```markdown
# 09 — Message Events

## Scenario A: Message Intermediate Catch
A workflow waits for an external message (correlated by `requestId`). Sending the message via API unblocks the workflow.

## Scenario B: Message Boundary Event
A long timer (60s) has a boundary message event. Sending a cancel message interrupts the timer and takes the cancel path.

## Prerequisites
- Aspire stack running
- Note the API port from Aspire dashboard

## Steps — Scenario A (message-catch.bpmn)

### 1. Deploy and start
- Import `message-catch.bpmn`, deploy, start `message-catch-test`
- Instance should be **Running** with `waitApproval` active

### 2. Send message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "approvalReceived", "correlationKey": "req-456", "variables": {}}'
```
Expected: `{"delivered": true}`

### 3. Verify outcome (refresh)
- [ ] Instance status: **Completed**
- [ ] `waitApproval` and `afterApproval` in completed activities
- [ ] Variables: `requestId` = **"req-456"**

## Steps — Scenario B (message-boundary.bpmn)

### 1. Deploy and start
- Import `message-boundary.bpmn`, deploy, start `message-boundary-test`
- Instance should be **Running** with `longWait` active

### 2. Send cancel message via API
```bash
curl -X POST http://localhost:<port>/workflow/message \
  -H "Content-Type: application/json" \
  -d '{"messageName": "cancelRequest", "correlationKey": "req-789", "variables": {}}'
```

### 3. Verify outcome (refresh)
- [ ] Instance status: **Completed**
- [ ] `cancelPath` in completed activities (boundary message interrupted the timer)
- [ ] `normalPath` NOT in completed activities
- [ ] Variables: `cancelled` = **true**
```

**Step 4: Commit**

```bash
git add tests/manual/09-message-events/
git commit -m "test: add manual test for message events (09)"
```

---

### Task 11: 10-signal-events

**Files:**
- Create: `tests/manual/10-signal-events/signal-catch-throw.bpmn`
- Create: `tests/manual/10-signal-events/signal-boundary.bpmn`
- Create: `tests/manual/10-signal-events/test-plan.md`

**Step 1: Create signal-catch-throw.bpmn**

One workflow that waits for a signal. We broadcast the signal via API.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <signal id="Signal_1" name="globalAlert" />
  <process id="signal-catch-test">
    <startEvent id="start" />
    <intermediateCatchEvent id="waitSignal">
      <signalEventDefinition signalRef="Signal_1" />
    </intermediateCatchEvent>
    <scriptTask id="afterSignal" scriptFormat="csharp">
      <script>_context.signalReceived = true</script>
    </scriptTask>
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="waitSignal" />
    <sequenceFlow id="f2" sourceRef="waitSignal" targetRef="afterSignal" />
    <sequenceFlow id="f3" sourceRef="afterSignal" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create signal-boundary.bpmn**

A long timer with a boundary signal event.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <signal id="Signal_2" name="emergencyStop" />
  <process id="signal-boundary-test">
    <startEvent id="start" />
    <intermediateCatchEvent id="longWait">
      <timerEventDefinition>
        <timeDuration>PT60S</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <boundaryEvent id="signalBoundary" attachedToRef="longWait">
      <signalEventDefinition signalRef="Signal_2" />
    </boundaryEvent>
    <task id="normalPath" />
    <scriptTask id="emergencyPath" scriptFormat="csharp">
      <script>_context.emergency = true</script>
    </scriptTask>
    <endEvent id="end1" />
    <endEvent id="end2" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="longWait" />
    <sequenceFlow id="f2" sourceRef="longWait" targetRef="normalPath" />
    <sequenceFlow id="f3" sourceRef="signalBoundary" targetRef="emergencyPath" />
    <sequenceFlow id="f4" sourceRef="normalPath" targetRef="end1" />
    <sequenceFlow id="f5" sourceRef="emergencyPath" targetRef="end2" />
  </process>
</definitions>
```

**Step 3: Create test-plan.md**

```markdown
# 10 — Signal Events

## Scenario A: Signal Catch
A workflow waits for signal `globalAlert`. Broadcasting the signal via API unblocks it.

## Scenario B: Signal Boundary
A long timer has a boundary signal `emergencyStop`. Broadcasting the signal interrupts the timer.

## Prerequisites
- Aspire stack running
- Note the API port

## Steps — Scenario A (signal-catch-throw.bpmn)

### 1. Deploy and start
- Import `signal-catch-throw.bpmn`, deploy, start `signal-catch-test`
- Instance **Running** with `waitSignal` active

### 2. Broadcast signal via API
```bash
curl -X POST http://localhost:<port>/workflow/signal \
  -H "Content-Type: application/json" \
  -d '{"signalName": "globalAlert"}'
```
Expected: `{"deliveredCount": 1}`

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] `afterSignal` in completed activities
- [ ] Variables: `signalReceived` = **true**

## Steps — Scenario B (signal-boundary.bpmn)

### 1. Deploy and start
- Import `signal-boundary.bpmn`, deploy, start `signal-boundary-test`
- Instance **Running** with `longWait` active

### 2. Broadcast signal via API
```bash
curl -X POST http://localhost:<port>/workflow/signal \
  -H "Content-Type: application/json" \
  -d '{"signalName": "emergencyStop"}'
```

### 3. Verify outcome
- [ ] Instance status: **Completed**
- [ ] `emergencyPath` completed (signal boundary fired)
- [ ] `normalPath` NOT completed
- [ ] Variables: `emergency` = **true**
```

**Step 4: Commit**

```bash
git add tests/manual/10-signal-events/
git commit -m "test: add manual test for signal events (10)"
```

---

### Task 12: 11-error-boundary

**Files:**
- Create: `tests/manual/11-error-boundary/child-that-fails.bpmn`
- Create: `tests/manual/11-error-boundary/error-on-call-activity.bpmn`
- Create: `tests/manual/11-error-boundary/test-plan.md`

**Step 1: Create child-that-fails.bpmn**

A child process with a script that throws an exception.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="child-that-fails">
    <startEvent id="childStart" />
    <scriptTask id="failingScript" scriptFormat="csharp">
      <script>throw new System.Exception("Something went wrong")</script>
    </scriptTask>
    <endEvent id="childEnd" />
    <sequenceFlow id="cf1" sourceRef="childStart" targetRef="failingScript" />
    <sequenceFlow id="cf2" sourceRef="failingScript" targetRef="childEnd" />
  </process>
</definitions>
```

**Step 2: Create error-on-call-activity.bpmn**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="error-boundary-test">
    <startEvent id="start" />
    <callActivity id="callFailing" calledElement="child-that-fails" />
    <boundaryEvent id="errorBoundary" attachedToRef="callFailing">
      <errorEventDefinition />
    </boundaryEvent>
    <task id="happyEnd" />
    <scriptTask id="errorHandler" scriptFormat="csharp">
      <script>_context.errorHandled = true</script>
    </scriptTask>
    <endEvent id="end1" />
    <endEvent id="end2" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="callFailing" />
    <sequenceFlow id="f2" sourceRef="callFailing" targetRef="happyEnd" />
    <sequenceFlow id="f3" sourceRef="errorBoundary" targetRef="errorHandler" />
    <sequenceFlow id="f4" sourceRef="happyEnd" targetRef="end1" />
    <sequenceFlow id="f5" sourceRef="errorHandler" targetRef="end2" />
  </process>
</definitions>
```

**Step 3: Create test-plan.md**

```markdown
# 11 — Error Boundary Event

## Scenario
A parent process calls a child that throws an exception. An error boundary event on the call activity catches the error and routes to an error-handling path. The parent does NOT fail.

## Prerequisites
- Aspire stack running
- **Deploy `child-that-fails.bpmn` FIRST**

## Steps

### 1. Deploy child process
- Import `child-that-fails.bpmn`, deploy

### 2. Deploy parent process
- Import `error-on-call-activity.bpmn`, deploy

### 3. Start the parent
- Start `error-boundary-test`, open Instance Viewer

### 4. Verify outcome
- [ ] Parent instance status: **Completed** (NOT failed)
- [ ] `errorHandler` in completed activities (error boundary caught the exception)
- [ ] `happyEnd` NOT in completed activities
- [ ] Variables: `errorHandled` = **true**
- [ ] Activities tab: `callFailing` shows error details (code 500, message "Something went wrong")
- [ ] BPMN canvas highlights the error path
```

**Step 4: Commit**

```bash
git add tests/manual/11-error-boundary/
git commit -m "test: add manual test for error boundary events (11)"
```

---

### Task 13: 12-variable-scoping

**Files:**
- Create: `tests/manual/12-variable-scoping/parallel-variable-isolation.bpmn`
- Create: `tests/manual/12-variable-scoping/test-plan.md`

**Step 1: Create parallel-variable-isolation.bpmn**

Two parallel branches each set the same variable name to different values.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <process id="variable-scoping-test">
    <startEvent id="start" />
    <scriptTask id="initVar" scriptFormat="csharp">
      <script>_context.shared = "original"</script>
    </scriptTask>
    <parallelGateway id="fork" />
    <scriptTask id="branchA" scriptFormat="csharp">
      <script>_context.shared = "from-branch-A"</script>
    </scriptTask>
    <scriptTask id="branchB" scriptFormat="csharp">
      <script>_context.shared = "from-branch-B"</script>
    </scriptTask>
    <parallelGateway id="join" />
    <endEvent id="end" />
    <sequenceFlow id="f1" sourceRef="start" targetRef="initVar" />
    <sequenceFlow id="f2" sourceRef="initVar" targetRef="fork" />
    <sequenceFlow id="f3" sourceRef="fork" targetRef="branchA" />
    <sequenceFlow id="f4" sourceRef="fork" targetRef="branchB" />
    <sequenceFlow id="f5" sourceRef="branchA" targetRef="join" />
    <sequenceFlow id="f6" sourceRef="branchB" targetRef="join" />
    <sequenceFlow id="f7" sourceRef="join" targetRef="end" />
  </process>
</definitions>
```

**Step 2: Create test-plan.md**

```markdown
# 12 — Variable Scoping

## Scenario
A variable `shared` is set before a parallel fork. Each branch overwrites `shared` with a different value. Verifies that parallel branches get isolated variable scopes (cloned at fork), so neither branch's write affects the other.

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy and start
- Import `parallel-variable-isolation.bpmn`, deploy, start `variable-scoping-test`

### 2. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Variables tab shows **multiple variable scopes** (separate scope IDs)
- [ ] One scope has `shared` = **"from-branch-A"**
- [ ] Another scope has `shared` = **"from-branch-B"**
- [ ] The scopes are isolated — branch A's write did not affect branch B
- [ ] Original scope has `shared` = **"original"**
```

**Step 3: Commit**

```bash
git add tests/manual/12-variable-scoping/
git commit -m "test: add manual test for variable scoping (12)"
```

---

### Task 14: Final commit with all files

**Step 1: Verify all files are present**

```bash
find tests/manual -type f | sort
```

Expected: 17 files (12 test-plan.md + 15 .bpmn files... actually let me count: 1+1+1+1+1+2+1+2+2+2+2+1 = 17 bpmn + 12 test-plan = 29 files total, but some tasks share the commit).

**Step 2: Final review commit (if any unstaged changes remain)**

```bash
git status
```

If clean, done. If anything unstaged, add and commit.
