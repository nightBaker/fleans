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
