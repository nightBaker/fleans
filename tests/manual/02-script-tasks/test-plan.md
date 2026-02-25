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
