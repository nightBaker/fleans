# 16 - Message Start Event

## Scenario
A workflow is triggered by an incoming message rather than an explicit start request. When a message matching the start event's message name is sent, a new workflow instance is automatically created and started.

## Prerequisites
- Aspire stack running (`dotnet run --project Fleans.Aspire`)
- API available at `https://localhost:7140`

## Steps

1. **Deploy the BPMN workflow**
   - Open the Web UI and upload `message-start-event.bpmn`
   - Verify the process `message-start-process` appears in the deployments list

2. **Send a message to trigger workflow creation**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/message \
     -H "Content-Type: application/json" \
     -d '{"MessageName":"orderReceived","Variables":{"orderId":"ORD-001"}}'
   ```

3. **Verify instance was created**
   - Response should contain `"Delivered": true` and a `WorkflowInstanceIds` array with one GUID
   - Open the Web UI and verify the new workflow instance is visible
   - The instance should have completed (msgStart → processOrder → end)

4. **Send another message to create a second instance**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/message \
     -H "Content-Type: application/json" \
     -d '{"MessageName":"orderReceived","Variables":{"orderId":"ORD-002"}}'
   ```
   - Verify a new, separate instance is created

5. **Send a message with no matching start event**
   ```bash
   curl -k -X POST https://localhost:7140/Workflow/message \
     -H "Content-Type: application/json" \
     -d '{"MessageName":"unknownMessage","Variables":{}}'
   ```
   - Should return 404

## Expected Outcomes

- [ ] BPMN deploys successfully with message start event
- [ ] Sending `orderReceived` message creates a new workflow instance
- [ ] Response includes `WorkflowInstanceIds` with the created instance ID
- [ ] Each message creates a separate instance
- [ ] Message variables are available as workflow variables
- [ ] Unknown message name returns 404
- [ ] No correlation key is required for message start events
