---
title: Service Tasks
description: How to complete service tasks in Fleans using external workers and the REST API.
---

## What are service tasks?

A **service task** (`<bpmn:serviceTask>`) represents automated work in a BPMN process — calling an API, processing a payment, sending an email, etc.

## How they work in Fleans

Fleans treats service tasks as **external-completion tasks**. When a workflow instance reaches a service task, the engine pauses the token there and waits for an external worker to complete it via the REST API.

There is no in-process handler interface to implement. Instead, the pattern is:

1. The workflow instance reaches a `<bpmn:serviceTask>` and marks it as an active activity.
2. An external worker calls `POST /Workflow/complete-activity` with the activity result.
3. The engine merges the returned variables into the workflow scope and advances the token.

This decoupled model lets you write workers in any language and scale them independently of the engine.

:::note
Fleans does not currently have a dedicated "list active service tasks" API endpoint. Your worker needs to know the `WorkflowInstanceId` and `ActivityId` to complete a task — typically received via a message broker, webhook, or by querying instance state. See the [C# worker example](#c-backgroundservice-worker) below for one approach.
:::

## BPMN example

```xml
<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                  xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                  xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                  id="Definitions_1"
                  targetNamespace="http://bpmn.io/schema/bpmn">
  <bpmn:process id="order-process" isExecutable="true">
    <bpmn:startEvent id="start" />
    <bpmn:serviceTask id="charge-payment" name="Charge Payment" />
    <bpmn:endEvent id="end" />
    <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="charge-payment" />
    <bpmn:sequenceFlow id="f2" sourceRef="charge-payment" targetRef="end" />
  </bpmn:process>
  <bpmndi:BPMNDiagram id="Diagram_1">
    <bpmndi:BPMNPlane id="Plane_1" bpmnElement="order-process">
      <bpmndi:BPMNShape id="start_di" bpmnElement="start">
        <dc:Bounds x="180" y="100" width="36" height="36" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="charge_di" bpmnElement="charge-payment">
        <dc:Bounds x="270" y="78" width="100" height="80" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id="end_di" bpmnElement="end">
        <dc:Bounds x="430" y="100" width="36" height="36" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1">
        <di:waypoint x="216" y="118" />
        <di:waypoint x="270" y="118" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id="f2_di" bpmnElement="f2">
        <di:waypoint x="370" y="118" />
        <di:waypoint x="430" y="118" />
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</bpmn:definitions>
```

Deploy via the Admin UI (Blazor editor) — open the **Web app** from the Aspire dashboard, navigate to the **Editor** page, import the BPMN XML, and click **Deploy**.

Then start an instance:

```bash
curl -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"order-process"}'
```

The response includes a `workflowInstanceId`. The instance is now paused at `charge-payment`.

## Completing a service task with curl

```bash
curl -X POST https://localhost:7140/Workflow/complete-activity \
  -H "Content-Type: application/json" \
  -d '{
    "WorkflowInstanceId": "<instance-guid>",
    "ActivityId": "charge-payment",
    "Variables": { "paymentRef": "ch_abc123" }
  }'
```

The engine merges `paymentRef` into the workflow variables and moves to the end event.

## C# BackgroundService worker

The example below shows a worker that receives work items via a message queue and completes service tasks. In a real system, the queue message would contain the `WorkflowInstanceId` and `ActivityId` — pushed by a webhook, message broker, or another service that started the workflow.

```csharp
public sealed class PaymentWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PaymentWorker> _logger;

    public PaymentWorker(
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient("fleans");

        while (!stoppingToken.IsCancellationRequested)
        {
            // In production, receive work items from a message broker
            // (e.g., RabbitMQ, Azure Service Bus, Kafka).
            // Each message contains the WorkflowInstanceId and ActivityId.
            var workItem = await DequeueWorkItemAsync(stoppingToken);
            if (workItem is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            _logger.LogInformation("Processing {ActivityId} for instance {Id}",
                workItem.ActivityId, workItem.WorkflowInstanceId);

            // --- your business logic here ---
            var paymentRef = $"ch_{Guid.NewGuid():N}";

            await client.PostAsJsonAsync("Workflow/complete-activity", new
            {
                workItem.WorkflowInstanceId,
                workItem.ActivityId,
                Variables = new { paymentRef }
            }, stoppingToken);
        }
    }

    // Placeholder — replace with your actual message broker consumer.
    private Task<WorkItem?> DequeueWorkItemAsync(CancellationToken ct) =>
        Task.FromResult<WorkItem?>(null);
}

public record WorkItem(Guid WorkflowInstanceId, string ActivityId);
```

Register it in `Program.cs`:

```csharp
builder.Services.AddHttpClient("fleans", c =>
    c.BaseAddress = new Uri("https://localhost:7140/"));
builder.Services.AddHostedService<PaymentWorker>();
```

## Best practices

- **Idempotency** — design workers so that completing the same task twice is harmless. The engine rejects duplicate completions (409 Conflict), but your side effects should be safe to retry.
- **Error handling** — if your worker encounters an error, decide whether to retry or to call the fail-activity path so the workflow can route through an error boundary event.
- **Scaling** — because workers are decoupled from the engine, you can run multiple replicas without any coordination.
- **Discovery** — Fleans does not expose a "list pending service tasks" API. Design your integration so that the caller who starts the workflow instance passes the `WorkflowInstanceId` to the worker via a message queue or callback URL.
