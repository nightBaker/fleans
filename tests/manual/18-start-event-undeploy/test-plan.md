# 18 — Start Event Undeploy (Disable/Enable)

## Scenario
Verify that disabling a process definition stops its start event listeners and enabling re-registers them.

## Prerequisites
- Aspire stack running: `dotnet run --project Fleans.Aspire`
- Web UI at https://localhost:7175
- API at https://localhost:7140

## Steps

### 1. Deploy the fixture
Upload `signal-start-disable.bpmn` via Web UI Editor or API.

### 2. Verify signal starts a new instance
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response includes a `WorkflowInstanceIds` list with one ID
- [ ] Instance visible in Web UI under `signal-start-disable-test`

### 3. Disable the process
```
POST https://localhost:7140/Workflow/disable
{"ProcessDefinitionKey": "signal-start-disable-test"}
```
- [ ] Response `IsActive` is `false`
- [ ] Web UI shows "Disabled" badge and dimmed row

### 4. Verify signal no longer starts instances
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response: 404 (no subscription or start event found)
- [ ] No new instances created

### 5. Verify manual start is blocked
```
POST https://localhost:7140/Workflow/start
{"WorkflowId": "signal-start-disable-test"}
```
- [ ] Response: error (process is disabled)
- [ ] Web UI Start button is disabled

### 6. Re-enable the process
```
POST https://localhost:7140/Workflow/enable
{"ProcessDefinitionKey": "signal-start-disable-test"}
```
- [ ] Response `IsActive` is `true`
- [ ] Web UI badge removed, row no longer dimmed

### 7. Verify signal starts instances again
```
POST https://localhost:7140/Workflow/signal
{"SignalName": "test-disable-signal"}
```
- [ ] Response includes `WorkflowInstanceIds` with one ID
- [ ] Instance visible in Web UI
