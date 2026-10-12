# Fleans.Plugins.CamundaExternalTask

Camunda 7 External Task compatibility for Fleans. A `<serviceTask type="camunda:<topic>">` is
queued in the plugin's own `camunda_external_tasks` table and served through the Camunda 7
`/engine-rest/external-task` REST API (`fetchAndLock`, `complete`, `failure`, `bpmnError`,
`extendLock`, `lock`, `unlock`, `retries`, `priority`, queries). Existing Camunda external-task
workers keep working with only their base URL changed. The engine has no Camunda-specific code.

## Minimum consumer usage

In a plugin host (`Fleans:Role=Plugin`) `Program.cs`:

```csharp
using Fleans.Plugins.CamundaExternalTask;

builder.Services.AddCamundaExternalTasks(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("camunda-external-tasks")));

var app = builder.Build();
app.MapCamundaExternalTaskApi();   // → /engine-rest/external-task/*
```

BPMN: replace `camunda:type="external" camunda:topic="charge-card"` with `type="camunda:charge-card"`.

See the [migration guide](https://nightbaker.github.io/fleans/guides/camunda-external-tasks/)
for semantics and differences from Camunda 7. Without incidents, `retries = 0` fails the
activity, and variables sent with `bpmnError` are ignored.

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical.
