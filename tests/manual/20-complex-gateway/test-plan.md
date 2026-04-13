# Complex Gateway

## Scenario 20a: Fork with conditional and default flows (fork-conditional.bpmn)

Tests that the complex gateway fork evaluates conditional outgoing flows and activates matching branches; when no condition matches, the default flow is taken.

### Prerequisites
- Aspire stack running

### Steps
1. Deploy `fork-conditional.bpmn`
2. Start an instance of `complex-gateway-fork` (variable `x = 7` is set by setup task)

### Expected
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, setup, fork, highBranch, endHigh
- [ ] `defaultBranch` does NOT appear in completed activities (`x > 5` is true, so default is not taken)
- [ ] Variable `high` = `true`
- [ ] No `defaultTaken` variable

---

### Steps (default path variant)
1. Redeploy or start a new instance with `x = 3` (modify setup script)
2. All conditional flows evaluate to false → default flow `s3` is taken

### Expected (default path)
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, setup, fork, defaultBranch, endDefault
- [ ] Variable `defaultTaken` = `true`
- [ ] No `high` variable

---

## Scenario 20b: Join with activationCondition fires on first token (join-activation-condition.bpmn)

Tests that the complex gateway join with `activationCondition="_context._nroftoken >= 1"` fires as soon as the first token arrives, discarding the later-arriving token.

### Prerequisites
- Aspire stack running

### Steps
1. Deploy `join-activation-condition.bpmn`
2. Start an instance of `complex-gateway-join-activation`

### Expected
- [ ] Instance status: **Completed**
- [ ] Completed activities include: start, fork, fastTask, slowTask, join, afterJoin, end
- [ ] Variable `joined` = `true`
- [ ] The gateway fires after the first token arrives; the second token is silently discarded (no error, no deadlock)
- [ ] Instance does not stall waiting for a second token

---

## Scenario 20c: Join without activationCondition (parallel-style)

Uses any 2-branch parallel fork joined by a `complexGateway` with no `activationCondition`. Should behave identically to a parallel gateway join — waits for ALL tokens.

### Steps
1. Build a minimal BPMN inline: parallel fork → task1 + task2 → complexGateway (no activationCondition) → end
2. Deploy and start

### Expected
- [ ] Instance status: **Completed** only after both tasks finish
- [ ] No premature completion after the first token
