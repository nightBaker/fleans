# 17 - Signal Start Event

## Scenario
A workflow is triggered by a broadcast signal rather than an explicit start request. When a signal matching the start event's signal name is sent, a new workflow instance is automatically created and started.

## Prerequisites
- Aspire stack running (`dotnet run --project Fleans.Aspire`)
- API available at `https://localhost:7140`

## Steps

1. **Deploy the BPMN workflow**
   - Open the Web UI and upload `signal-start-event.bpmn`
   - Verify the process `signal-start-process` appears in the deployments list

2. **Send a signal to trigger workflow creation**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/signal \
     -H "Content-Type: application/json" \
     -d '{"SignalName":"orderSignal"}'
   ```

3. **Verify instance was created**
   - Response should contain `WorkflowInstanceIds` array with one GUID
   - Open the Web UI and verify the new workflow instance is visible
   - The instance should have completed (sigStart -> task1 -> end)

4. **Send a second signal to create another instance**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/signal \
     -H "Content-Type: application/json" \
     -d '{"SignalName":"orderSignal"}'
   ```
   - Verify a new, separate instance is created

5. **Send a signal with an unknown name**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/signal \
     -H "Content-Type: application/json" \
     -d '{"SignalName":"unknownSignal"}'
   ```
   - Should return 404

## Expected Outcomes

- [ ] BPMN deploys successfully with signal start event
- [ ] Sending `orderSignal` signal creates a new workflow instance
- [ ] Response includes `WorkflowInstanceIds` with the created instance ID
- [ ] Each signal creates a separate instance
- [ ] Unknown signal name returns 404
