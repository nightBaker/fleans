# Multi-Instance Activity (Parallel)

## Scenario 13a: Collection-based parallel

Tests parallel multi-instance over a collection with output aggregation.

### Prerequisites
- None

### 1. Deploy the workflow
- Open Workflows page
- Click "Create New"
- Import `parallel-collection.bpmn` via drag-drop
- Click Deploy, confirm

### 2. Start an instance
- On Workflows page, click "Start" for `parallel-collection-test`
- Navigate to the instance viewer

### 3. Verify outcome
- [ ] Instance status: Completed
- [ ] Completed activities: start, setItems, reviewTasks (3 iterations), end
- [ ] Variables tab: `results` contains `["reviewed-A","reviewed-B","reviewed-C"]`
- [ ] No error activities

---

## Scenario 13b: Cardinality-based parallel

Tests parallel multi-instance with fixed loop count.

### Prerequisites
- None

### 1. Deploy the workflow
- Open Workflows page
- Click "Create New"
- Import `parallel-cardinality.bpmn` via drag-drop
- Click Deploy, confirm

### 2. Start an instance
- On Workflows page, click "Start" for `parallel-cardinality-test`
- Navigate to the instance viewer

### 3. Verify outcome
- [ ] Instance status: Completed
- [ ] Completed activities: start, repeatTask (3 iterations), end
- [ ] No error activities
