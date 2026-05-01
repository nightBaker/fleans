---
title: Writing custom-task plugins
description: Step-by-step tutorial for shipping your own <serviceTask type="..."> plugin against the Fleans custom-task framework.
---

<!--
  The HelloWorld example in this guide is intentionally NOT committed to the repo.
  Code blocks are verified by building a scratch project against the matching
  framework SHA. If a future framework PR changes a signature, this guide will
  drift silently — re-verify against `git show HEAD:src/Fleans/Fleans.Worker/CustomTasks/`
  before publishing changes.
-->

This tutorial walks an author from an empty class library to a working `<bpmn:serviceTask type="hello">` that runs a .NET plugin in-process on a Worker silo. End-to-end takes about 15 minutes the first time.

## Choosing the right pattern

Fleans gives you two ways to back a `<bpmn:serviceTask>`:

| Need | Pattern |
|---|---|
| Worker is non-.NET (Python, Node, Go) | External completion ([Service Tasks](/guides/service-tasks/)) |
| Worker pool needs to scale independently of the engine | External completion |
| Queue or message bus between engine and worker (Kafka, RabbitMQ, etc.) | External completion |
| Worker is .NET, runs in-process on a Worker silo | **Custom-task plugin (this guide)** |
| Need a typed parameter editor in the management UI | **Custom-task plugin** |
| Want per-task-type schema discoverable via `GET /custom-tasks` | **Custom-task plugin** |

If your situation isn't on this list, lean toward external completion — it's the more decoupled option. Custom-task plugins are the right fit when you have a lot of small per-task-type behaviors that ship together with the engine and want first-class editor support for them.

## Prerequisites

- Fleans repo cloned locally and the Aspire stack runs (`dotnet run --project Fleans.Aspire` from `src/Fleans/`).
- .NET 10 SDK.
- An editor / IDE that handles C# (Rider, VS, VS Code).

## What you'll build

A trivial "say hello" plugin. The workflow author writes:

```xml
<bpmn:serviceTask id="greet" type="hello">
  <bpmn:extensionElements>
    <zeebe:taskDefinition type="hello" />
    <zeebe:ioMapping>
      <zeebe:input  source="=customerName"     target="name" />
      <zeebe:input  source="true"              target="excitement" />
      <zeebe:output source="=__response.text"  target="greeting" />
    </zeebe:ioMapping>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

Your plugin reads `name` (`String`) and `excitement` (`Boolean`), and returns `{ "text": "Hello, alice!!" }`. The output mapping pulls `text` out and writes it to the workflow variable `greeting`.

## Step 1 — Create the plugin project

From `src/Fleans/`:

```bash
dotnet new classlib -n Fleans.Plugins.HelloWorld -f net10.0
dotnet sln add Fleans.Plugins.HelloWorld/Fleans.Plugins.HelloWorld.csproj
cd Fleans.Plugins.HelloWorld
dotnet add reference ../Fleans.Worker/Fleans.Worker.csproj
dotnet add package Microsoft.Orleans.Sdk --version 10.0.1
```

Your `.csproj` should now look like:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>14</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Orleans.Sdk" Version="10.0.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Fleans.Worker\Fleans.Worker.csproj" />
  </ItemGroup>
</Project>
```

## Step 2 — Write the handler

Add `HelloWorldHandler.cs`:

```csharp
using System.Dynamic;
using Fleans.Application.Events;
using Fleans.Worker.CustomTasks;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.HelloWorld;

[ImplicitStreamSubscription(WorkflowEventsPublisher.ExecuteCustomTaskStreamNamespace)]
[WorkerPlacement]
public sealed partial class HelloWorldHandler(
    ILogger<HelloWorldHandler> logger,
    IGrainFactory grainFactory)
    : CustomTaskHandlerBase(logger, grainFactory)
{
    private readonly ILogger<HelloWorldHandler> _logger = logger;

    protected override string TaskType => "hello";

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CancellationToken cancellationToken)
    {
        var name = resolvedInputs.TryGetValue("name", out var n) ? n?.ToString() : "world";
        var excited = resolvedInputs.TryGetValue("excitement", out var e)
            && string.Equals(e?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var text = excited ? $"Hello, {name}!!" : $"Hello, {name}.";

        LogGreeted(name ?? "(null)", excited);

        var response = new ExpandoObject();
        ((IDictionary<string, object?>)response)["text"] = text;

        return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["__response"] = response,
        });
    }

    [LoggerMessage(EventId = 9400, Level = LogLevel.Information,
        Message = "HelloWorld plugin greeted {Name} (excited={Excited})")]
    private partial void LogGreeted(string name, bool excited);
}
```

Things worth noticing:

- `partial class` because of the `[LoggerMessage]` source generator.
- **`[ImplicitStreamSubscription(WorkflowEventsPublisher.ExecuteCustomTaskStreamNamespace)]` and `[WorkerPlacement]` MUST be repeated on the concrete handler.** `CustomTaskHandlerBase` carries both, but Orleans's grain-class discovery walks concrete types and doesn't reliably honor attribute inheritance from an abstract base — without the explicit attributes on the subclass, the handler is never activated when an event arrives.
- The returned dictionary's `__response` key is what output mappings read. Plugins are free to put whatever shape they like under `__response` (string, primitive, or a nested object as shown).
- Throw `Fleans.Domain.Errors.CustomTaskFailedActivityException(string code, string message)` to fail with a typed error code routable via boundary error events. Any other thrown exception fails with code `"500"`.

## Step 3 — Declare the parameter schema

Add `HelloWorldSchema.cs`:

```csharp
using Fleans.Application.CustomTasks;

namespace Fleans.Plugins.HelloWorld;

public static class HelloWorldSchema
{
    public static readonly CustomTaskParameterSchema Default = new(new[]
    {
        new CustomTaskParameterSpec(
            Name: "name",
            DisplayName: "Recipient name",
            Type: CustomTaskParameterType.String,
            Required: false,
            Description: "Greeting target; defaults to \"world\".",
            DefaultValue: "world"),

        new CustomTaskParameterSpec(
            Name: "excitement",
            DisplayName: "Add exclamation",
            Type: CustomTaskParameterType.Boolean,
            Required: false,
            Description: "If true, two exclamation points; if false, a period.",
            DefaultValue: "false"),
    });
}
```

The schema is what the management UI's BPMN editor will read to render typed widgets. The five primitive types are `String`, `Integer`, `Boolean`, `Expression`, `MultilineString`. Repeat-allowed parameters use `List` or `Map` with an `ItemType` (see [Custom Tasks reference](/concepts/custom-tasks/) for the parameter table).

## Step 4 — Wire DI

Add `HelloWorldServiceCollectionExtensions.cs`:

```csharp
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.HelloWorld;

public static class HelloWorldServiceCollectionExtensions
{
    public static IServiceCollection AddHelloWorldPlugin(this IServiceCollection services) =>
        services.AddCustomTaskPlugin<HelloWorldHandler>(
            taskType: "hello",
            displayName: "Say hello",
            parameterSchema: HelloWorldSchema.Default);
}
```

Register the plugin from the Fleans API host. Open `src/Fleans/Fleans.Api/Fleans.Api.csproj` and add a project reference:

```xml
<ProjectReference Include="..\Fleans.Plugins.HelloWorld\Fleans.Plugins.HelloWorld.csproj" />
```

Then in `src/Fleans/Fleans.Api/Program.cs`, add the using and the registration:

```csharp
using Fleans.Plugins.HelloWorld;

// … later, alongside the other AddXxx calls:
builder.Services.AddHelloWorldPlugin();
```

## Step 5 — Restart Aspire

`CustomTaskPluginRegistrar` runs once at silo startup, at Orleans's `ServiceLifecycleStage.Active`, and announces the plugin to the catalog grain. So after wiring DI you need to restart the silo before the plugin appears anywhere.

```bash
dotnet build
dotnet run --project Fleans.Aspire
```

Once Aspire is up, hit the catalog API directly to confirm:

```bash
curl -k https://localhost:7140/custom-tasks
```

Expect a JSON entry with `taskType: "hello"`, `displayName: "Say hello"`, and `siloNames` listing your local silo. Or open `https://localhost:7124/admin/custom-tasks` in a browser for the same data rendered as a table.

## Step 6 — Author the BPMN with the editor

Open `https://localhost:7124/editor`. Drag a Service Task onto the canvas and click it.

In the right-hand properties panel, find the "Plugin (custom task)" dropdown and pick **Say hello (hello)**. The editor seeds the defaults — your task now has `<zeebe:input source="world" target="name"/>` and `<zeebe:input source="false" target="excitement"/>` written under `<zeebe:ioMapping>`.

Type `=customerName` into the `name` field. The help text under each primitive widget reminds you the field accepts either a literal value or `=variableName`. Toggle `excitement` on with the checkbox.

Add an `<zeebe:output source="=__response.text" target="greeting"/>` row by editing the BPMN XML directly (the editor's UI for output mappings is shared with `expectedOutputs` on User Tasks; see [BPMN Editor](/guides/editor/)).

Save the diagram. The BPMN XML now contains the snippet from the [What you'll build](#what-youll-build) section above.

## Step 7 — Run a workflow that uses the plugin

Deploy the BPMN:

```bash
curl -k -X POST https://localhost:7140/Workflow/deploy \
  -H "Content-Type: application/json" \
  -d "{\"BpmnXml\": $(jq -Rs . < /path/to/your.bpmn)}"
```

Start an instance:

```bash
curl -k -X POST https://localhost:7140/Workflow/start \
  -H "Content-Type: application/json" \
  -d '{"WorkflowId":"<your-process-id>","Variables":{"customerName":"alice"}}'
```

Inspect the result:

```bash
curl -k https://localhost:7140/Workflow/instances/<instance-id>/state
```

The variable projection includes `greeting: "Hello, alice!!"`. Watch the Aspire log for the `[9400] HelloWorld plugin greeted alice (excited=True)` entry your `[LoggerMessage]` writes.

## Troubleshooting

**The plugin doesn't appear in the catalog UI.**

Check that `services.AddHelloWorldPlugin()` is wired in the API host's `Program.cs` — the registrar only runs for silos that have the descriptor in DI. If you're running a Core/Worker split topology, the registration must be on the Worker host that physically loads the handler.

**`<serviceTask type="hello">` deploys but the activity hangs forever.**

Two common causes:
1. No silo with this plugin registered. `GET /custom-tasks` returns no entry for `"hello"` — fix the DI wiring per the previous bullet.
2. The plugin handler crashed during stream subscription. Check Aspire logs for stack traces in `OnActivateAsync` or in the constructor (e.g. failed dependency injection). Implicit-stream subscriptions silently abort when the grain throws on activate.

**Plugin throws `CustomTaskFailedActivityException` with the wrong code.**

The exception's first argument is a string for the code (`"400"`, `"503"`, etc.). Boundary error events match by `errorCode` string equality, so authors typically choose HTTP-shaped codes. Codes outside `[100, 599]` will surface but won't be matched by HTTP-style boundary events.

**`<zeebe:input>` not parsed at deploy time.**

Two possible causes:
1. The BPMN file is missing `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"` on `<bpmn:definitions>`. Add the namespace declaration.
2. The `target` attribute isn't a valid identifier (`^[a-zA-Z_][a-zA-Z0-9_]*$`). Rename or fix the typo.

**Catalog shows the plugin on a stopped silo.**

The catalog reconciles every 30 s against `IManagementGrain.GetDetailedHosts()` and drops entries for silos no longer in the cluster. Wait one tick.

## Where to next

- [Custom Tasks reference](/concepts/custom-tasks/) — parameter type table, mapping grammar, failure semantics, what-lives-where, catalog & liveness internals.
- Manual test plans: `tests/manual/37-custom-task-framework/` (framework smoke), `tests/manual/38-custom-task-editor/` (editor UX).
- Source: `src/Fleans/Fleans.Worker/CustomTasks/` for the framework; any merged plugin (`Fleans.Plugins.RestCaller` if it lands later) as a real-world example.
- File a [feature request](https://github.com/nightBaker/fleans/issues) if you hit a limitation that isn't on the list above.
