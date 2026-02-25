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
