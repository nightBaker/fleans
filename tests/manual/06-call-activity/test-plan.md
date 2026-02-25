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
