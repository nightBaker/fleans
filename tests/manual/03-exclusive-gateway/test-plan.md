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
