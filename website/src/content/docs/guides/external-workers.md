---
title: External Workers (gRPC)
description: Every custom task runs on a worker connected over gRPC — in .NET with the Fleans.ExternalTasks.Sdk package, or in any language from the protocol file. The protocol and delivery contract.
---

Every custom task runs on an **external worker**, in the spirit of Zeebe's job workers and Camunda
7's external tasks: a process that
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

| | Workers (gRPC) | [REST completion only](/fleans/guides/service-tasks/) |
|---|---|---|
| Worker language | any gRPC client; .NET SDK | any |
| How the worker finds work | Fleans streams it to the worker | your own channel |
| Typed parameters in the editor | yes, announced by the worker | no |
| Listed in the custom-task catalog | yes, while connected | no |

For a .NET tutorial, see [Writing custom tasks](/fleans/guides/writing-custom-tasks/).

## 1. Reach the API

There is nothing to turn on. The gRPC service is served by `Fleans.Api` on its HTTP endpoint, as
plain gRPC (HTTP/2) and as gRPC-Web (HTTP/1.1, which passes any proxy). When the API has
`Authentication:Authority` set, workers send a bearer token like any other API client. The
timeouts are in the [configuration reference](/fleans/reference/configuration/#external-tasks).

## 2. Model the task

A service task with a `type` (or `zeebe:taskDefinition`) goes to the workers of that type. The worker receives the task's **input mappings**, already evaluated, and
the [variables it declared](#choose-which-variables-a-worker-receives), none by default:

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
            throw new ExternalTaskFailedException("PAYMENT_DECLINED", charge.Reason); // BPMN error code
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
`AddExternalTaskHandler` to give the editor typed inputs. `category`, `visibility` and
`allowedProcessKeys` are described in [Categories and private plugins](/fleans/concepts/custom-tasks/#categories-and-private-plugins);
over the protocol they are the `category`, `visibility` and `allowed_process_keys` fields of `TaskTypeRegistration`.
A public task that declares allowed processes is rejected.

### Version your worker and declare its parameters

Give a handler a version and a parameter schema, and its parameters become a contract: the engine
checks a task's inputs against them, at deploy and again just before sending the task.

```csharp
builder.Services.AddExternalTaskHandler<ChargeHandler>(
    "charge-card",
    version: "1.4.0",
    parameterSchema: new ExternalTaskParameterSchema(
    [
        new ExternalTaskParameter("amount", "Amount", ExternalTaskParameterType.Integer, Required: true),
        new ExternalTaskParameter("customerId", "Customer", ExternalTaskParameterType.String, Required: true),
    ]));
```

A version is a [SemVer](https://semver.org) `major.minor.patch` with an optional prerelease; build
metadata (`1.4.0+abc`) is ignored. A version that isn't one throws at registration. Over the protocol
it is the `version` field of `TaskTypeRegistration`.

**Which worker runs a task.** The author picks `latest`, the default, or an exact version in the
editor, or writes it in the BPMN:

```xml
<zeebe:taskDefinition type="charge-card" version="1.4.0" />
```

| The task asks for | It runs on |
|---|---|
| `latest` | The workers of the highest connected stable version. If no stable version is connected, the highest prerelease; if none has a version, any worker of the type. |
| An exact version | Only workers of that version. With none connected the task waits for one, like a task with no worker at all. A worker with no version never matches. |

**Rolling out a new version.** The first worker of a new version takes every `latest` task as soon as
it connects, whether or not the rest of the fleet has caught up, so it alone carries that load until
the others join. Keep the new parameters able to accept what running processes send, or pin those
processes to the old version.

**Same version, other parameters.** A version's contract can't change while it is connected. A
worker announcing a connected version with different parameter names, types, required flags or item
types is refused: `Subscribe` ends with `FailedPrecondition`, for its whole stream, and the SDK logs
it and retries after `ReconnectDelay`, so it connects once the conflicting worker is gone. Only
workers connected now count; display names, descriptions and defaults may differ. Workers with no
version are never refused: they can disagree during an upgrade, and then their inputs are not
checked. A wildcard handler has no version and no contract, and its tasks are not checked.

**What the check rejects.** At deploy, a task whose inputs don't fit fails the deploy with a `400`
naming each one, and no version is stored. A check that can't run is a warning on the deploy, not a
failure: no worker or no worker of the pinned version is connected, or versionless workers disagree.

Just before sending a task, once its expressions have values, the check runs again against the worker
that would get it. On a mismatch the task fails with code `PLUGIN_INPUTS_INVALID`, without calling the
worker and without redelivery. Catch it with an [error boundary](/fleans/guides/error-handling/) like
any other code. The message names the inputs and the types of their values, never the values
themselves. A handler that declares no schema has no contract, so nothing is checked for it.

The rules are in [Custom Tasks](/fleans/concepts/custom-tasks/#the-input-contract).

**Known limit.** A `latest` task follows the newest version connected at that moment. If a process was
deployed against 2.0.0 and only 1.0.0 is connected when the task runs, it goes to 1.0.0, and if its
inputs don't fit it fails with `PLUGIN_INPUTS_INVALID` rather than waiting for 2.0.0. Pin the version
when that matters.

### Choose which variables a worker receives

A handler receives no workflow variables unless it asks for them, so large variables don't cross
the wire with every task. Declare what the code needs when you register it:

```csharp
builder.Services.AddExternalTaskHandler<ChargeHandler>("charge-card");   // none: `variables` is empty

builder.Services.AddExternalTaskHandler<ChargeHandler>("charge-card",
    variables: ExternalTaskVariables.Names("orderId", "amount"));        // these top-level variables

builder.Services.AddExternalTaskHandler<ChargeHandler>("charge-card",
    variables: ExternalTaskVariables.All);                               // everything visible to the task
```

Names match exactly, and a name no visible variable has is left out. Input mappings always arrive in
`resolvedInputs`, whatever the handler declares: the process author writes those, and the handler
author declares what the code reads, so editing a BPMN file can't change what a handler receives.

Over the protocol these are the `all_variables` and `variable_names` fields of
`TaskTypeRegistration`; with neither set, `variables_json` is an empty object. A registration that
sets both, or a blank name, is rejected. The declaration belongs to each worker, so workers of one
type can differ during a rolling upgrade.

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
[`external_task_worker.proto`](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ExternalTasks.Sdk/Protos/external_task_worker.proto)
(package `fleans.externaltasks.v1`, also shipped in the `Fleans.ExternalTasks.Sdk` NuGet
package under `protos/`). The loop is:

1. Call `Subscribe` once per process, with a `worker_name` and the task types you implement. Keep
   the stream open: it carries a `TaskAssignment` per task, a `TaskCancellation` when the engine
   stops waiting for one, and a `Heartbeat` every few seconds.
2. For each assignment, do the work, then call `CompleteTask` (with a JSON `result_json`) or
   `FailTask` (with a BPMN error `code`), quoting the assignment's `task_token`. Or call `DeferTask`
   when the outcome [comes later](#tasks-that-finish-later).
3. When the stream drops, subscribe again after a pause.

## Delivery contract

**At least once, so handlers must be idempotent.** A task is delivered again — possibly to another
worker — when its worker disconnects or crashes before reporting, or does not report within
`Fleans:ExternalTasks:ExecutionTimeout` (5 minutes by default; the worker gets a cancellation
first). Key your side effects on the activity instance id, which stays the same across deliveries;
a report for a task that has already finished is ignored.

**A task with no connected worker waits, for a limited time.** The token stays on the service task
and the task is offered again on each stream redelivery, so a worker that connects within the
stream's delivery window (`Fleans__Streaming__MaxEventDeliveryTime`, 6 minutes by default) picks it
up. **Past the window, the engine stops offering that task for good, without logging it.** The
activity stays active, and a worker that connects later does not get it: settle it with
`POST /Execution/complete-activity` or `fail-activity`. If workers can be away longer, raise the
window on every silo, keeping in mind that a retrying event holds up its stream queue while it
waits. Put a timer boundary event on the task if the process must not wait.

**Failures are BPMN errors.** `FailTask` (in .NET, throwing `ExternalTaskFailedException`)
fails the activity with that error code, catchable by an error boundary event or error event
sub-process. Any other exception in a .NET handler fails it with code `500`. A cancelled task is
not a failure: it is not reported and is delivered again.

**REST still works.** A worker that took a task can also report it over
`POST /Execution/complete-activity` or `fail-activity` with the task's `ActivityInstanceId` —
see the [API reference](/fleans/reference/api/#post-executionfail-activity).

## Tasks that finish later

When a task's outcome arrives after the handler returns (a person, or an existing system that
polls for work), call `DeferTask`, or in .NET return `ExternalTaskHandler.Deferred`:

```csharp
await inbox.AddIfMissingAsync(context.ActivityInstanceId, resolvedInputs, cancellationToken);
return Deferred;
```

The engine stops waiting, and the activity stays active with no timeout until something calls
`POST /Execution/complete-activity` or `fail-activity` with its `ActivityInstanceId`. Record the
task idempotently first: a redelivery can still run the handler again. While holding it,
`GET /Execution/{workflowInstanceId}/activities/{activityInstanceId}/variables` returns all the
variables it sees, whatever the handler declared, or 404 once the activity is no longer active, for example after a boundary event interrupts it.

## Wildcard task types

A worker can subscribe to `<scheme>:*`, for example `AddExternalTaskHandler<T>("camunda:*")`, to
serve every type starting with `<scheme>:`. A worker claiming the exact type goes first. A wildcard
is not listed in the custom-task catalog, because there is no single type for an author to pick. It
has no version or contract: a pinned version on the task is ignored and its inputs are not checked.

## Observability

| EventId | Level | Meaning |
|---|---|---|
| 5002 | Debug | Task published to the external-task stream |
| 14500 / 14501 | Information / Debug | Task dispatched / assigned to a worker |
| 14502 | Warning | No connected worker took the task; it is delivered again |
| 14504 / 14505 | Warning | The worker disconnected / did not report within the execution timeout |
| 14514 | Information | The worker deferred the task; it waits for a REST report |
| 14540 / 14541 | Information | A worker connected / disconnected |
| 9326 / 9327 | Information | A worker's task type entered / left the custom-task catalog |

`GET /custom-tasks/{taskType}` lists the connected workers of a type under `siloNames`.
