# Multi-Instance Activity

## Scenario 13a: Collection-based parallel

Tests parallel multi-instance over a collection with output aggregation.

### Prerequisites
- None

### Steps
1. Deploy `parallel-collection.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, processItem (3 iterations), end
- [ ] Variables: `results` contains `["processed-A","processed-B","processed-C"]`

---

## Scenario 13b: Cardinality-based parallel

Tests parallel multi-instance with fixed loop count.

### Prerequisites
- None

### Steps
1. Deploy `parallel-cardinality.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, repeatTask (3 iterations), end

---

## Scenario 13c: Sequential collection

Tests sequential multi-instance over a collection.

### Prerequisites
- None

### Steps
1. Deploy `sequential-collection.bpmn`
2. Start an instance

### Expected
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, processItem (3 iterations), end
- [ ] Variables: `results` contains ordered output

---

## Scenario 13d: Iteration failure

Tests that when a multi-instance iteration fails, remaining sibling iterations are cancelled and the host fails.

### Prerequisites
- Create a BPMN with a parallel MI (cardinality=3) where one iteration throws an exception

### Steps
1. Deploy the failure test BPMN
2. Start an instance
3. Wait for iterations to complete

### Expected
- [ ] The failing iteration has error state (code 500)
- [ ] Remaining active sibling iterations are cancelled
- [ ] MI host entry is completed (failed)
- [ ] No active activities remain
- [ ] Workflow does not proceed to the next activity after the MI
