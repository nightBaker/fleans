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
