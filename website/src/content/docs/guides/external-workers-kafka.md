---
title: External Workers over Kafka
description: Hand service tasks to workers in any language through a Kafka topic per task type, and complete them over REST.
---

Some service tasks are best done by someone else's code: a payments service, a Python model, a
team that ships on its own schedule. These are Zeebe's job workers and Camunda 7's external
tasks. With an **external-task queue**, Fleans publishes each such task to a Kafka topic per
task type. Workers read the topic with their own consumer group, do the work, and complete or
fail the task over the REST API. The workers pull the work, so the engine holds no connections
to them and is not called when a worker looks for work.

```
service task "charge-card" reached
   │  publish {activityInstanceId, variables, …}
   ▼
Kafka topic  fleans.tasks.charge-card      ◀── workers (consumer group "payments")
                                                │ POST /Execution/complete-activity
Fleans ◀────────────────────────────────────────┘   or /Execution/fail-activity
```

Kafka is the first queue provider. Other queues can be added later behind the same switch,
`Fleans:ExternalTasks:Provider`.

## When to use it

| | External workers over Kafka | [Custom-task plugins](/fleans/guides/writing-custom-tasks/) | [REST completion only](/fleans/guides/service-tasks/) |
|---|---|---|---|
| Worker language | any Kafka client | .NET | any |
| How the worker finds work | reads a Kafka topic | Fleans calls the plugin | your own channel |
| Runs in the Fleans cluster | no | yes, in a plugin host | no |
| Typed parameters in the editor | no | yes | no |

## 1. Turn it on

Set these on the **core silos**. Fleans then publishes the listed task types to Kafka. Every
other type goes to its plugin as before.

```yaml
# docker compose / Helm extraEnv
Fleans__ExternalTasks__Provider: Kafka
Fleans__ExternalTasks__Kafka__Brokers: "kafka:9092"
Fleans__ExternalTasks__TaskTypes__0: charge-card
Fleans__ExternalTasks__TaskTypes__1: ship-order
```

On startup Fleans creates the missing topics `fleans.tasks.<type>`, with 8 partitions and
replication factor 3 by default. A single-broker cluster needs
`Fleans__ExternalTasks__Kafka__ReplicationFactor: "1"`; where topics are provisioned out of band,
set `Fleans__ExternalTasks__Kafka__CreateTopics: "false"`. Security settings go through as
librdkafka keys, for example `Fleans__ExternalTasks__Kafka__Config__security.protocol: SASL_SSL`.
All the keys are in the [configuration reference](/fleans/reference/configuration/#external-tasks).

The external-task queue does not depend on the stream provider. It works the same with Redis,
Kafka or Azure Queue streams.

## 2. Model the task

A service task with a `type` (or `zeebe:taskDefinition`) is an external task when its type is
listed in `TaskTypes`. The worker receives the task's **input mappings**, so map in what the
worker needs:

```xml
<bpmn:serviceTask id="charge" name="Charge card">
  <bpmn:extensionElements>
    <zeebe:taskDefinition type="charge-card" />
    <zeebe:ioMapping>
      <zeebe:input source="=order.total" target="amount" />
      <zeebe:input source="=order.customerId" target="customerId" />
      <zeebe:output source="=paymentRef" target="paymentRef" />
    </zeebe:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

Output mappings apply to the variables the worker sends back, as for any task.

## The task record

Each record is keyed by the workflow instance id, so the tasks of one instance stay in order on
one partition. The value is JSON:

```json
{
  "workflowInstanceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "workflowId": "order-process",
  "processDefinitionId": "order-process:3:…",
  "activityInstanceId": "9b2c4e1a-6f3d-4b8e-a1c2-7d5e9f0a1b2c",
  "activityId": "charge",
  "taskType": "charge-card",
  "businessId": "ORDER-1042",
  "variables": { "amount": 42.5, "customerId": "c-17" }
}
```

`activityInstanceId` identifies this run of the task. Send it back with the result, and use it
as the idempotency key for your side effects (the charge, the e-mail).

## 3. Complete or fail the task

```bash
# Done — variables are merged (through the output mappings) and the token moves on.
curl -X POST "$FLEANS/Execution/complete-activity" -H "Content-Type: application/json" -d '{
  "WorkflowInstanceId": "3fa85f64-…", "ActivityId": "charge",
  "ActivityInstanceId": "9b2c4e1a-…", "Variables": { "paymentRef": "ch_123" } }'

# Failed — a BPMN error, caught by a matching error boundary event or error event sub-process.
curl -X POST "$FLEANS/Execution/fail-activity" -H "Content-Type: application/json" -d '{
  "WorkflowInstanceId": "3fa85f64-…", "ActivityId": "charge",
  "ActivityInstanceId": "9b2c4e1a-…", "ErrorCode": "PAYMENT_DECLINED",
  "ErrorMessage": "card declined", "Variables": { "declineReason": "insufficient-funds" } }'
```

Both calls are safe to repeat. A completion or failure for an activity instance that has already
finished — completed, failed, or cancelled by a boundary event — answers `200` and changes
nothing. Retry on `429` (admission control is shedding load), `5xx` and dropped connections. See
the [API reference](/fleans/reference/api/#post-executionfail-activity).

## Who is responsible for what

**Fleans gets the task into Kafka.** The producer is idempotent and waits for every in-sync
replica (`acks=all`). It retries on its own for up to `message.timeout.ms`, 10 seconds by default.
If the broker still cannot be reached, the service task **fails** (log 1169), and an error boundary
event or error event sub-process can catch it. A token never waits on a task that no worker will
ever see. Keep `Fleans__ExternalTasks__Kafka__Config__message.timeout.ms` well under 30 seconds:
the publish happens while the workflow instance is busy.

**The worker gets the task done.** Fleans does not publish a task again. Once the record is in
Kafka, it stays there until the worker's consumer group commits past it. So:

- Commit an offset only after the REST report for that record succeeded. A worker that dies
  mid-task then leaves the record to the next member of its group.
- When tasks run in parallel, commit per partition only up to the oldest record still in flight.
  The sample worker shows one way to do this.
- Retry the report on `429`, `5xx` and dropped connections until it passes. The work is done
  already, and the report is safe to repeat.
- For work that cannot succeed, call `fail-activity` with an error code instead of dropping the
  record.

Delivery is therefore **at least once**. After a crash or a consumer-group rebalance, a worker
can receive a task again. Key your side effects on `activityInstanceId`; the repeated completion
is then ignored. A task cancelled while a worker holds it (say, an interrupting timer boundary
fired) stays in the topic, and its completion is ignored.

## Writing a worker

Any Kafka client works. The loop is:

1. Subscribe to `fleans.tasks.<type>` with a consumer group per kind of worker. Run as many
   workers in the group as the topic has partitions.
2. For each record: do the work, then `POST /Execution/complete-activity` (or `fail-activity`)
   with `activityInstanceId`.
3. Commit the offset after the REST call succeeded, in order within each partition.

A C# sample worker is in the plugins repository at
[`samples/Fleans.Samples.KafkaWorker`](https://github.com/nightBaker/fleans-plugins/tree/main/samples/Fleans.Samples.KafkaWorker).
It uses only Confluent.Kafka and `HttpClient`, runs up to `CONCURRENCY` tasks at a time,
commits offsets in order per partition and retries `429`/`5xx` with backoff:

```bash
KAFKA_BROKERS=kafka:9092 FLEANS_URL=http://fleans-api:8080 TASK_TYPE=charge-card \
  dotnet run --project samples/Fleans.Samples.KafkaWorker   # in a fleans-plugins checkout
```

## Observability

| EventId | Level | Meaning |
|---|---|---|
| 5002 | Debug | Task published to the external-task queue |
| 1169 | Error | Task could not be published; the service task failed |
| 14510 | Information | Task topics created at startup |
| 14511 | Warning | Kafka producer error |

Consumer-group lag on `fleans.tasks.<type>` shows whether the workers keep up.
