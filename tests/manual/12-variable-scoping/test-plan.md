# 12 — Variable Scoping

## Scenario
A variable `shared` is set before a parallel fork. Each branch overwrites `shared` with a different value. Verifies that:
1. Parallel branches get isolated variable scopes (cloned at fork) during execution
2. At the join gateway, branch scopes are merged back into the original scope (last-write-wins in token creation order) and branch scopes are removed

## Prerequisites
- Aspire stack running

## Steps

### 1. Deploy and start
- Import `parallel-variable-isolation.bpmn`, deploy, start `variable-scoping-test`

### 2. Verify outcome
- [ ] Instance status: **Completed**
- [ ] Variables tab shows **1 merged variable scope** (branch scopes removed after join)
- [ ] The merged scope contains `shared` with one of the branch values (last branch in creation order wins)
- [ ] Both branches' variables are present in the merged scope
- [ ] No orphaned branch scopes remain after completion
