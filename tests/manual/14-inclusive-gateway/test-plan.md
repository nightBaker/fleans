# Inclusive Gateway

## Scenario 14a: Parallel conditions (parallel-conditions.bpmn)

Tests that the inclusive fork evaluates all 3 conditions, activates only the 2 true branches, and the join waits for all active branches before proceeding.

### Prerequisites
- Aspire stack running

### Steps
1. Deploy `parallel-conditions.bpmn`
2. Start an instance of `inclusive-gateway-parallel-conditions`

### Expected
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, setup, fork, branch1, branch2, join, afterJoin, end
- [ ] `branch3` does NOT appear in completed activities (condition `a < 5` is false)
- [ ] Variables: `path1` = `"taken"`, `path2` = `"taken"`, `joined` = `true`
- [ ] No `path3` variable (branch3 was never activated)
- [ ] BPMN canvas highlights: start → setup → fork → branch1/branch2 → join → afterJoin → end

---

## Scenario 14b: Default flow (default-flow.bpmn)

Tests that when all conditions evaluate to false, the default flow is taken and the workflow completes through the default path.

### Prerequisites
- Aspire stack running

### Steps
1. Deploy `default-flow.bpmn`
2. Start an instance of `inclusive-gateway-default-flow`

### Expected
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, setup, fork, defaultTask, defaultEnd
- [ ] `highTask` and `lowTask` do NOT appear in completed activities
- [ ] Variables: `path` = `"default"`
- [ ] BPMN canvas highlights the default flow path
