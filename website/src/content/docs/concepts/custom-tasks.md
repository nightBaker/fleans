---
title: Custom Tasks
description: Plug arbitrary side-effecting work (HTTP, queues, file IO) into a BPMN workflow via <serviceTask type="...">.
---

A **custom task** is a `<serviceTask>` whose `type` attribute names a plugin registered with the
engine. The plugin is an Orleans grain that implements `ICustomTaskCallProvider`; the engine
dispatches to it on a stream, hands it the resolved input map, lets it populate `__response`, then
projects output mappings back into workflow variables. From the workflow author's perspective it
behaves like any other task — start, run, complete (or fail and route to a boundary).

## Why this exists

Real workflows usually need more than scripts and gateways. They call HTTP services, push to
queues, read databases, send notifications. Embedding any of those into the engine binds the
core to specific transports and dependency versions. The custom-task framework keeps the engine
free of those concerns: plugins live in their own assemblies, declare their own grain interfaces,
and ship in their own deploy units.

## Authoring a workflow

```xml
<serviceTask id="create-user" type="rest-call">
  <extensionElements>
    <zeebe:ioMapping>
      <zeebe:input  source="=email"             target="email_address" />
      <zeebe:input  source="=&quot;POST&quot;"  target="method" />
      <zeebe:output source="=__response.body.id" target="created_user_id" />
    </zeebe:ioMapping>
  </extensionElements>
</serviceTask>
```

The `type="rest-call"` attribute selects the plugin. The `<zeebe:ioMapping>` block is parsed at
deploy time — malformed mappings (empty source, invalid identifier targets, `__response` reserved
on output) reject the entire deploy with `400 Bad Request`.

## Mapping grammar

Mapping `source` accepts four forms:

| Form              | Meaning                                                 |
|-------------------|---------------------------------------------------------|
| `=identifier`     | Top-level lookup against the scope                      |
| `=path.to.field`  | Dot-walk through nested dictionaries / `ExpandoObject`s |
| `="literal"`      | Quoted string literal                                   |
| `=42` `=true` `=null` | Primitive literal (long, double, bool, null)        |
| `bare-string`     | String literal (no leading `=`)                         |

Mapping `target` must be a valid identifier (`^[a-zA-Z_][a-zA-Z0-9_]*$`). On `output`, the
`__response` name is reserved — providers populate it during execution, output mapping projects
*from* it but never *to* it.

> **`${expr}` is condition-expression syntax — not for io mappings.** Conditional sequence flows
> use `${x > 5}` (rewritten by the BPMN converter to `_context.x > 5` for DynamicExpresso). Custom
> task io mappings use the `=expr` grammar above. Mixing them produces a deploy-time validation
> error.

## Authoring a plugin

A plugin is two assemblies:

- **`*.Core`** — declares the per-plugin grain interface inheriting from `ICustomTaskCallProvider`.
  This is the contract the Api host references for registration. It must NOT pull in the
  implementation.
- **`*.Worker`** — implements the grain. This assembly is loaded only by Worker silos.

```csharp
// In *.Core
public interface IRestCallerGrain : ICustomTaskCallProvider { }

// In *.Worker
[StatelessWorker]
public class RestCallerGrain : Grain, IRestCallerGrain
{
    public async Task ExecuteAsync(IDictionary<string, object?> resolved, ExpandoObject variables)
    {
        // resolved is pre-populated with input mappings (e.g. resolved["email_address"]).
        // The provider does its work and writes __response for output mapping to project from.
        var email = (string?)resolved["email_address"];
        var response = await CallApi(email);
        resolved["__response"] = ToExpando(response);
    }
}
```

## Registering a plugin

```csharp
services.AddCustomTaskProvider<IRestCallerGrain>("rest-call");
```

The `taskType` discriminator (`"rest-call"`) is matched case-insensitively against
`<serviceTask type="...">`. Convention: lowercase-with-hyphens.

> **Registry initialisation timing.** `CustomTaskCallProviderRegistry` is a DI singleton whose
> constructor takes `IEnumerable<CustomTaskRegistration>`. The injected enumeration is captured at
> first resolution, which means **all `AddCustomTaskProvider<T>` calls must complete before the
> registry is first resolved**. In production this is host startup (the DI graph is built before
> any grain accepts traffic). In tests with a custom DI builder, ensure all registrations execute
> before the first `provider.GetRequiredService<CustomTaskCallProviderRegistry>()` call.

## Failure semantics

The handler wraps provider invocation in a `try/catch` and routes to `IWorkflowInstanceGrain.FailActivity`
on any exception. Two kinds of failure surface differently:

- **`CustomTaskFailedActivityException(int code, string message)`** — recommended for provider
  errors that should round-trip a specific HTTP-style code (e.g. `400` for malformed inputs the
  provider rejects, `503` for transient upstream failures). The engine's `FailActivity` plumbing
  reads `GetActivityErrorState()` off the exception, so the code lands in the boundary error
  payload without further plumbing.
- **Anything else** — the raw exception flows to `FailActivity` and the engine assigns
  `code=500, message=ex.Message`.

The handler retries the activity if the *fail* path itself throws, so the engine's idempotency
guards stay intact.

## Today's placement story

Provider grains run wherever Orleans places them. PR #367 added the structural worker split
(separate `Fleans.Worker` assembly + `Fleans:Role` config), but runtime placement filtering — gating
provider grains to Worker silos via a `WorkerPlacement` directive — is a v2 follow-up alongside
cancellation/timeout. Today, a Core silo will host a provider grain if Orleans routes one there.
For most plugins this is fine; if your plugin must NOT run on the Core silo (e.g. it loads heavy
native dependencies), keep it out of the Core silo's reference graph until the v2 work lands.

## Limitations (v1)

- **No cancellation.** `ICustomTaskCallProvider.ExecuteAsync(IDictionary, ExpandoObject)` does not
  take a `CancellationToken`. Long-running providers will not be interrupted by silo shutdown.
- **No timeout.** There is no `CustomTaskOptions.Timeout`; providers must self-manage upper bounds
  on their work.
- **No placement filter.** Providers run on whichever silo Orleans picks, not exclusively Worker
  silos.

All three land together in a v2 follow-up issue.
