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
2. An external worker polls or is notified, then calls `POST /Workflow/complete-activity` with the activity result.
3. The engine merges the returned variables into the workflow scope and advances the token.

This decoupled model lets you write workers in any language and scale them independently of the engine.

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
      <bpmndi:BPMNEdge id="f2_di" bpmnElement="charge-payment">
        <di:waypoint x="370" y="118" />
        <di:waypoint x="430" y="118" />
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</bpmn:definitions>
```

Deploy and start the workflow:

```bash
# Deploy
curl -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d @order-process.bpmn

# Start an instance
curl -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"order-process"}'
```

The instance is now paused at `charge-payment`.

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

The example below polls the Fleans admin UI API for pending tasks and completes any `charge-payment` activities it finds. In production you would use a message broker or webhook instead of polling.

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
        // Base address configured in DI: https://localhost:7140

        while (!stoppingToken.IsCancellationRequested)
        {
            var tasks = await client.GetFromJsonAsync<List<PendingTask>>(
                "Workflow/tasks", stoppingToken);

            foreach (var task in tasks ?? [])
            {
                if (task.ActivityId != "charge-payment")
                    continue;

                _logger.LogInformation("Processing {ActivityId} for instance {Id}",
                    task.ActivityId, task.WorkflowInstanceId);

                // --- your business logic here ---
                var paymentRef = $"ch_{Guid.NewGuid():N}";

                await client.PostAsJsonAsync("Workflow/complete-activity", new
                {
                    task.WorkflowInstanceId,
                    task.ActivityId,
                    Variables = new { paymentRef }
                }, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public record PendingTask(Guid WorkflowInstanceId, string ActivityId);
```

Register it in `Program.cs`:

```csharp
builder.Services.AddHttpClient("fleans", c =>
    c.BaseAddress = new Uri("https://localhost:7140/"));
builder.Services.AddHostedService<PaymentWorker>();
```

## Best practices

- **Idempotency** — design workers so that completing the same task twice is harmless. The engine rejects duplicate completions, but your side effects should be safe to retry.
- **Error handling** — if your worker encounters an error, decide whether to retry or to call the fail-activity path so the workflow can route through an error boundary event.
- **Scaling** — because workers are decoupled from the engine, you can run multiple replicas polling for tasks without any coordination.
