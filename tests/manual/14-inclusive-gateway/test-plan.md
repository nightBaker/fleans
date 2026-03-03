# Inclusive Gateway Manual Test Plan

## Scenario 1: Parallel Conditions (parallel-conditions.bpmn)

**Prerequisites:** Aspire running (`dotnet run --project Fleans.Aspire`)

**Steps:**
1. Deploy `parallel-conditions.bpmn` via Web UI
2. Start a workflow instance
3. Observe: the inclusive fork evaluates 3 conditions
4. Conditions 1 and 2 return true, condition 3 returns false
5. Verify: tasks on branches 1 and 2 execute
6. Complete both tasks
7. Verify: inclusive join proceeds, workflow completes

**Expected:**
- [ ] Fork waits for all conditions before transitioning
- [ ] Only true-condition branches are activated
- [ ] Join waits for all active branches
- [ ] Workflow completes after join

## Scenario 2: Default Flow (default-flow.bpmn)

**Steps:**
1. Deploy `default-flow.bpmn` via Web UI
2. Start a workflow instance
3. Observe: all conditions evaluate to false
4. Verify: default path is taken
5. Verify: workflow completes via default end event

**Expected:**
- [ ] All conditions evaluated (no short-circuit)
- [ ] Default flow taken when all false
- [ ] Workflow completes via default path
