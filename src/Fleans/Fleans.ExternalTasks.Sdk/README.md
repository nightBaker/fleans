# Fleans.ExternalTasks.Sdk

Write Fleans custom-task workers — the code behind `<bpmn:serviceTask type="…">`. A worker process
built on this package is a plain .NET generic host that connects to a Fleans API over
**gRPC** (or gRPC-Web): it receives the tasks of the types it implements, runs them, and reports
the outcome. It joins no Orleans cluster, so it needs no clustering, streaming or reminder
configuration — only the API's URL. The package has no Orleans dependency, direct or transitive.

## Minimum worker

```csharp
using System.Dynamic;
using Fleans.ExternalTasks.Sdk;

public sealed class MyHandler(IHttpClientFactory http) : ExternalTaskHandler
{
    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,   // the task's evaluated input mappings
        ExpandoObject variables,                         // the variables declared at registration; none by default
        ExternalTaskContext context,              // ids; ActivityInstanceId = idempotency key
        CancellationToken cancellationToken)
    {
        if (!resolvedInputs.ContainsKey("orderId"))
            throw new ExternalTaskFailedException("MISSING_ORDER", "orderId is required"); // BPMN error code
        // ... your work ...
        return new Dictionary<string, object?> { ["__response"] = new { ok = true } };
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddFleansExternalTaskWorker();                       // binds Fleans:ExternalTaskWorker
builder.Services.AddExternalTaskHandler<MyHandler>("my-task", displayName: "My Task");
builder.Build().Run();
```

```bash
Fleans__ExternalTaskWorker__EngineUrl=http://fleans-api:8080 dotnet run
```

Pass `version:` (SemVer) and `parameterSchema:` to make the task's parameters a contract the engine
enforces, and to let authors pin a version. See
[Version your worker](https://nightbaker.github.io/fleans/guides/external-workers/#version-your-worker-and-declare-its-parameters).

## Configuration (`Fleans:ExternalTaskWorker`)

| Key | Default | |
|---|---|---|
| `EngineUrl` | — (required) | The Fleans API base URL. |
| `WorkerName` | `{machine}-{pid}` | Shown in the custom-task catalog; unique per process. |
| `UseGrpcWeb` | `true` | gRPC-Web over HTTP/1.1 works against any API endpoint and proxy. Set `false` for native gRPC when the endpoint speaks HTTP/2 (TLS, or Kestrel `Protocols=Http2`). |
| `AccessToken` | — | Bearer token for an API with `Authentication:Authority` set (or set `AccessTokenProvider` in code). |
| `MaxConcurrentTasks` | `32` | Tasks run at once; further assignments wait in-process. |
| `ReconnectDelay` | `5s` | Pause before re-subscribing after the connection drops. |

## Delivery contract

- **At-least-once: handlers must be idempotent.** A task whose worker disconnects, crashes, or
  exceeds the engine's `Fleans:ExternalTasks:ExecutionTimeout` is delivered again — possibly to
  another worker. Key external side effects by `context.ActivityInstanceId`, which stays the same
  across deliveries.
- **No connected worker.** The task waits, within the engine's stream delivery window
  (`Fleans:Streaming:MaxEventDeliveryTime`, 6 minutes by default). Past it the engine stops offering
  the task, and the activity stays active until reported over REST.
- **Failures.** `ExternalTaskFailedException(code, message)` fails the activity with that
  BPMN error code (catchable by an error boundary event); any other exception fails it with `500`.
- **Cancellation is not failure.** The token fires when the engine stops waiting (timeout,
  shutdown), the connection drops, or the host stops. The task is not reported and is delivered
  again. After a dropped connection the worker re-subscribes after `ReconnectDelay`.
- **Deferring.** Return `ExternalTaskHandler.Deferred` when the outcome comes later: the activity
  stays active, with no timeout, until `POST /Execution/complete-activity` or `fail-activity` quotes
  `context.ActivityInstanceId`. A redelivery can still run the handler again, so record the task
  idempotently first.
- **Wildcards.** `AddExternalTaskHandler<T>("<scheme>:*")` claims every `<scheme>:…` type no worker
  claims exactly. A wildcard is not listed in the catalog.
- **Editor metadata.** `parameterSchema` (`ExternalTaskParameterSchema`) renders typed inputs;
  `category`, `visibility` (`ExternalTaskVisibility`) and `allowedProcessKeys` work as in the catalog.
- **Variables.** A handler receives no workflow variables unless it declares them:
  `variables: ExternalTaskVariables.Names("orderId", "amount")` or `ExternalTaskVariables.All`.
  Input mappings always arrive in `resolvedInputs`.
- Inputs, variables and the result cross the wire as JSON.

Other languages: generate a client from `protos/external_task_worker.proto`, shipped in this
package (`src/Fleans/Fleans.ExternalTasks.Sdk/Protos/`).

See the [external workers guide](https://nightbaker.github.io/fleans/guides/external-workers/).
