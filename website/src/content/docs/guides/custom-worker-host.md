---
title: Hosting Workers
description: Run your custom-task handlers as their own deployable. A worker is a plain process that connects to the Fleans API over gRPC.
---

A custom task runs on a **worker**: your own deployable, separate from the Fleans engine images. It
is a plain .NET generic host (or a process in any language) that connects to the Fleans API over
gRPC. It does not join the Orleans cluster, so it needs no clustering, reminder or streaming
settings, only the API's URL.

The **[`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example)**
repository is a GitHub template with the minimum shape for that deployable.

## Quick start

Click **Use this template** on the [example repo](https://github.com/nightBaker/fleans-custom-worker-example),
or via gh CLI:

```bash
gh repo create my-worker --template nightBaker/fleans-custom-worker-example --public
cd my-worker
Fleans__ExternalTaskWorker__EngineUrl="http://localhost:8081" dotnet run --project src/Fleans.CustomWorkerHost
```

That gives you a worker serving the `rest-call` task type from the template's pre-registered
[`Fleans.Plugins.RestCaller`](https://github.com/nightBaker/fleans-plugins/blob/main/docs/rest-caller/README.md) package.
Swap in your own handler (see [Writing custom tasks](/fleans/guides/writing-custom-tasks/))
and ship the resulting container image alongside the engine.

## Why a separate process

- **Operational isolation.** A misbehaving handler (memory leak, runaway HTTP call) only kills its
  own worker, never the engine.
- **Independent release cadence.** Deploy, scale and roll back a worker without touching the
  engine. Different teams can each ship their own.
- **No engine dependencies.** `Fleans.ExternalTasks.Sdk` has no Orleans dependency, direct or
  transitive. Verify the closure with:

  ```bash
  dotnet list package --include-transitive | grep -iE 'fleans|orleans'
  ```

  You should see `Fleans.ExternalTasks.Sdk` and your plugin packages, and nothing with Orleans in its name.

## Configuration

The worker's settings are under `Fleans:ExternalTaskWorker`; the
[table is in External Workers](/fleans/guides/external-workers/#3-write-a-net-worker). Only `EngineUrl` is required.

- **Point it at any API process.** `Combined`, `Core` and REST-only `Api` silos all serve workers.
- **Authentication.** When the API has `Authentication:Authority` set, set `AccessToken` or `AccessTokenProvider`.
- **Protocol.** `UseGrpcWeb` defaults to `true` (HTTP/1.1, passes any proxy). Set `false` for native gRPC over HTTP/2.

## Connection lifecycle

- **Startup order doesn't matter.** A worker retries until the API answers, every `ReconnectDelay` (5 seconds by default).
- **When the connection drops**, the worker cancels its running handlers and subscribes again. Those tasks are delivered again, possibly to another worker, so handlers [must be idempotent](/fleans/concepts/custom-tasks/#running-many-workers).
- **When a worker crashes** without closing the connection, the engine stops picking it once its lease expires (`Fleans:ExternalTasks:WorkerTimeout`, 45 seconds by default).
- **While no worker is connected**, tasks of that type wait, for a limited time. Start workers before running processes that use them, and read the [delivery contract](/fleans/guides/external-workers/#delivery-contract) if they can be away for more than a few minutes.

## Scaling

Run as many replicas as you need: each task goes to one connected worker of its type, picked at
random, and each process runs up to `MaxConcurrentTasks` tasks at once. Give each process a unique
`WorkerName`; the default (`{machine}-{pid}`) is.

## Adding a handler

1. Write your handler by deriving from `ExternalTaskHandler` (see
   [Writing custom tasks](/fleans/guides/writing-custom-tasks/)).
2. Publish it as a NuGet package, or `<ProjectReference>` it from your monorepo.
3. Register it in `Program.cs` with `services.AddExternalTaskHandler<T>(...)`, or the package's own extension such as `services.AddRestCallerPlugin()`.
4. Rebuild and redeploy the worker container.

## Versioning

`Fleans.ExternalTasks.Sdk` and the ready-made plugin packages share the engine's version track.
Workers talk to the engine over a versioned protocol (`fleans.externaltasks.v1`) rather than
sharing its cluster, so they don't have to be redeployed in lockstep. Check the release notes when a
release changes the protocol.

## Cross-references

- [`fleans-custom-worker-example`](https://github.com/nightBaker/fleans-custom-worker-example) — the GitHub template repo.
- [Custom Tasks concept page](/fleans/concepts/custom-tasks/) — the BPMN-side picture.
- [External Workers](/fleans/guides/external-workers/) — protocol, delivery contract, other languages.
