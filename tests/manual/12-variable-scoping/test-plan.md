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
