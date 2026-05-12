# Fleans.Domain.Abstractions

Leaf NuGet for Fleans plugin authors. Holds the minimum Domain surface a custom-task plugin may need:

- **Marker interfaces** — `IDomainEvent`
- **Event records carried on Orleans streams** — `ExecuteCustomTaskEvent`
- **Value records those events reference** — `InputMapping`, `OutputMapping`
- **Exception hierarchy plugins may throw** — `ActivityException`, `CustomTaskFailedActivityException`, `BadRequestActivityException`, `ActivityErrorState`

This package depends only on `Microsoft.Orleans.Sdk`.

## Where it fits

```
Fleans.Worker  →  Fleans.Application.Abstractions  →  Fleans.Domain.Abstractions
```

Plugin authors typically don't reference this package directly — they reference `Fleans.Worker`, and this package comes in transitively. It is published separately so the dependency closure is explicit and small.

## Related packages

- **`Fleans.Application.Abstractions`** — grain interfaces (script/condition/custom-task), schema records, plugin scaffolding.
- **`Fleans.Worker`** — `CustomTaskHandlerBase`, `[WorkerPlacement]`, placement directors. The package plugin authors actually consume.
