# Fleans.Plugins.RestCaller

Backs `<serviceTask type="rest-call">` BPMN service tasks with an outbound HTTP request.
Populates `__response` with `status`, `statusCode`, `ok`, `body`, and `headers`; throws a
typed `CustomTaskFailedActivityException` on non-success per the engine's failure-code
mapping.

## Minimum consumer usage

In your worker silo's `Program.cs`:

```csharp
using Fleans.Plugins.RestCaller;

builder.Services.AddRestCallerPlugin();
```

The plugin then claims `<serviceTask type="rest-call">` activities at runtime. See the
[Custom Tasks documentation](https://nightbaker.github.io/fleans/concepts/custom-tasks/)
for the full input/output schema.

> Plugin packages share the engine's `<VersionPrefix>` track — every Fleans release bumps
> every plugin's NuGet version even when the plugin source is bit-identical (same precedent
> as `Aspire.Hosting.*` / `Microsoft.Orleans.*`).
