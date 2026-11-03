# Fleans.ExternalTasks.Sdk

Write Fleans external-task workers — the code behind the `<bpmn:serviceTask type="…">` types the
engine routes to external workers (`Fleans:ExternalTasks:TaskTypes`). A worker process built on
this package is a plain .NET generic host that connects to a Fleans API over
**gRPC** (or gRPC-Web): it receives the tasks of the types it implements, runs them, and reports
the outcome. It joins no Orleans cluster, so it needs no clustering, streaming or reminder
configuration — only the API's URL.

```
Fleans.ExternalTasks.Sdk  →  Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

## Minimum worker

```csharp
using System.Dynamic;
using Fleans.Domain.Errors;
using Fleans.ExternalTasks.Sdk;

public sealed class MyHandler(IHttpClientFactory http) : ExternalTaskHandler
{
    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,   // the task's evaluated input mappings
        ExpandoObject variables,                         // workflow variables visible to the task
        ExternalTaskContext context,              // ids; ActivityInstanceId = idempotency key
        CancellationToken cancellationToken)
    {
        if (!resolvedInputs.ContainsKey("orderId"))
            throw new CustomTaskFailedActivityException("MISSING_ORDER", "orderId is required"); // BPMN error code
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

- **At-least-once.** A task whose worker disconnects, crashes, or exceeds the engine's
  `Fleans:ExternalTasks:ExecutionTimeout` is delivered again — possibly to another worker. Key
  external side effects by `context.ActivityInstanceId`, which stays the same across deliveries.
- **Failures.** `CustomTaskFailedActivityException(code, message)` fails the activity with that
  BPMN error code (catchable by an error boundary event); any other exception fails it with `500`.
- **Cancellation is not failure.** The token fires when the engine stops waiting (timeout,
  shutdown), the connection drops, or the host stops. The task is not reported and is delivered
  again.
- Inputs, variables and the result cross the wire as JSON.

Other languages: generate a client from `protos/external_task_worker.proto`, shipped in the
`Fleans.Application.Abstractions` package (`src/Fleans/Fleans.Application.Abstractions/ExternalTasks/Protos/`).

See the [external workers guide](https://nightbaker.github.io/fleans/guides/external-workers/).
