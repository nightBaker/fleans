---
title: External Workers (gRPC)
description: Hand service tasks to worker processes outside the cluster over gRPC — in .NET with the Fleans.ExternalTasks.Sdk package, or in any language from the protocol file.
---

Some service tasks are best done by someone else's code: a payments service, a Python model, a
team that ships on its own schedule. These are Zeebe's job workers and Camunda 7's external
tasks. Fleans hands each task of a configured type to an **external worker**: a process that
connects to the Fleans API over **gRPC**, receives the tasks of the types it implements, and
reports each outcome. A worker joins no Orleans cluster. It needs only the API's URL, so it can
be written in any language with a gRPC client and deployed on its own schedule.

```
service task "charge-card" reached
   │
   ▼
Fleans engine ── TaskAssignment ──▶ worker (Subscribe: one server stream per process)
      ▲                                │
      └──────── CompleteTask / FailTask ┘   (unary calls, quoting the task token)
```

## When to use it

| | External workers (gRPC) | [Custom-task plugins](/fleans/guides/writing-custom-tasks/) | [REST completion only](/fleans/guides/service-tasks/) |
|---|---|---|---|
| Worker language | any gRPC client; .NET SDK | .NET | any |
| How the worker finds work | Fleans streams it to the worker | Fleans calls the plugin | your own channel |
| Runs in the Fleans cluster | no — connects to the API | yes, in a plugin host | no |
| Typed parameters in the editor | yes, announced by the worker | yes | no |
| Listed in the custom-task catalog | yes, while connected | yes | no |

## 1. Turn it on

List the task types on the **engine silos** (the same value on every silo; `fleans-core` in
Docker Compose, `extraEnv` of the core and worker Deployments in Helm). Every other type goes
to its plugin as before.

```yaml
Fleans__ExternalTasks__TaskTypes__0: charge-card
Fleans__ExternalTasks__TaskTypes__1: ship-order
```

The gRPC service is served by `Fleans.Api` on its HTTP endpoint, as plain gRPC (HTTP/2) and
as gRPC-Web (HTTP/1.1, which passes any proxy). When the API has `Authentication:Authority`
set, workers send a bearer token like any other API client. The timeouts are in the
[configuration reference](/fleans/reference/configuration/#external-tasks).

## 2. Model the task

A service task with a `type` (or `zeebe:taskDefinition`) is an external task when its type is
listed in `TaskTypes`. The worker receives the task's **input mappings**, already evaluated, and
the variables visible to the task:

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

Output mappings apply to the result the worker sends back, as for any task. A task with no
output mappings merges the whole result into the workflow scope.

## 3. Write a .NET worker

Reference the [`Fleans.ExternalTasks.Sdk`](https://www.nuget.org/packages/Fleans.ExternalTasks.Sdk)
package in a plain generic host:

```csharp
using System.Dynamic;
using Fleans.Domain.Errors;
using Fleans.ExternalTasks.Sdk;

public sealed class ChargeHandler(IPaymentGateway payments) : ExternalTaskHandler
{
    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        var amount = Convert.ToDecimal(resolvedInputs["amount"]);
        // ActivityInstanceId stays the same when a task is delivered again: the idempotency key.
        var charge = await payments.ChargeAsync(amount, idempotencyKey: context.ActivityInstanceId, cancellationToken);
        if (charge.Declined)
            throw new CustomTaskFailedActivityException("PAYMENT_DECLINED", charge.Reason); // BPMN error code
        return new Dictionary<string, object?> { ["paymentRef"] = charge.Reference };
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IPaymentGateway, PaymentGateway>();
builder.Services.AddFleansExternalTaskWorker();                    // binds Fleans:ExternalTaskWorker
builder.Services.AddExternalTaskHandler<ChargeHandler>("charge-card", displayName: "Charge card");
builder.Build().Run();
```

```bash
Fleans__ExternalTaskWorker__EngineUrl=http://fleans-api:8080 dotnet run
```

The handler is resolved from a fresh DI scope for every task. Pass a `parameterSchema` to
`AddExternalTaskHandler` to give the editor typed inputs, as plugins do. `category`, `visibility` and
`allowedProcessKeys` work as they do for [plugins](/fleans/concepts/custom-tasks/#categories-and-private-plugins);
over the protocol they are the `category`, `visibility` and `allowed_process_keys` fields of `TaskTypeRegistration`.
A public task that declares allowed processes is rejected.

| Key (`Fleans:ExternalTaskWorker:…`) | Default | |
|---|---|---|
| `EngineUrl` | — (required) | The Fleans API base URL. |
| `WorkerName` | `{machine}-{pid}` | Shown in the custom-task catalog; unique per process. |
| `UseGrpcWeb` | `true` | gRPC-Web over HTTP/1.1 works against any endpoint and proxy. Set `false` for native gRPC when the endpoint speaks HTTP/2. |
| `AccessToken` | — | Bearer token for an API with authentication on (or set `AccessTokenProvider` in code). |
| `MaxConcurrentTasks` | `32` | Tasks run at once; further assignments wait in the process. |
| `ReconnectDelay` | `5s` | Pause before subscribing again after the connection drops. |

Run as many worker processes as you like: each task goes to one connected worker of its type,
picked at random.

## Other languages

Generate a client from
[`external_task_worker.proto`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.Application.Abstractions/ExternalTasks/Protos/external_task_worker.proto)
(package `fleans.externaltasks.v1`, also shipped in the `Fleans.Application.Abstractions` NuGet
package under `protos/`). The loop is:

1. Call `Subscribe` once per process, with a `worker_name` and the task types you implement. Keep
   the stream open: it carries a `TaskAssignment` per task, a `TaskCancellation` when the engine
   stops waiting for one, and a `Heartbeat` every few seconds.
2. For each assignment, do the work, then call `CompleteTask` (with a JSON `result_json`) or
   `FailTask` (with a BPMN error `code`), quoting the assignment's `task_token`.
3. When the stream drops, subscribe again after a pause.

## Delivery contract

**At least once.** A task is delivered again — possibly to another worker — when its worker
disconnects or crashes before reporting, or does not report within
`Fleans:ExternalTasks:ExecutionTimeout` (5 minutes by default; the worker gets a cancellation
first). Key your side effects on the activity instance id; a report for a task that has already
finished is ignored.

**A task with no connected worker waits.** The token stays on the service task and the task is
offered again on each stream redelivery, so a worker that connects within the stream's delivery
window (6 minutes) picks it up. Put a timer boundary event on the task if the process must not
wait that long.

**Failures are BPMN errors.** `FailTask` (in .NET, throwing `CustomTaskFailedActivityException`)
fails the activity with that error code, catchable by an error boundary event or error event
sub-process. Any other exception in a .NET handler fails it with code `500`. A cancelled task is
not a failure: it is not reported and is delivered again.

**REST still works.** A worker that took a task can also report it over
`POST /Execution/complete-activity` or `fail-activity` with the task's `ActivityInstanceId` —
see the [API reference](/fleans/reference/api/#post-executionfail-activity).

## Observability

| EventId | Level | Meaning |
|---|---|---|
| 5002 | Debug | Task published to the external-task stream |
| 14500 / 14501 | Information / Debug | Task dispatched / assigned to a worker |
| 14502 | Warning | No connected worker took the task; it is delivered again |
| 14504 / 14505 | Warning | The worker disconnected / did not report within the execution timeout |
| 14540 / 14541 | Information | A worker connected / disconnected |
| 9326 / 9327 | Information | A worker's task type entered / left the custom-task catalog |

`GET /custom-tasks/{taskType}` lists the connected workers of a type under `siloNames`.
