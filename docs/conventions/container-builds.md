# Container builds

Container images are produced by the .NET SDK's built-in `SdkContainerSupport` — **no Dockerfiles**. Each deployable service has a `<ContainerRepository>fleans-<svc></ContainerRepository>` in its csproj; `src/Fleans/Directory.Build.props` is the single source of truth for `<VersionPrefix>` and `<ContainerImageTag>`.

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj /t:PublishContainer /p:Version=0.1.0-test
# Same for Fleans.Web, Fleans.WorkerHost, Fleans.Mcp.

aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
aspire publish --project Fleans.Aspire -t kubernetes  -o out/k8s
```

**A failed start exits with code 1.** The images run `dotnet` as PID 1, which the runtime's own unhandled-exception path (`abort()`) does not kill: the host spun at 100 % CPU and `restart: on-failure` never fired (#943). `AddServiceDefaults` therefore registers `ExitOnUnhandledException` (print the exception, `Environment.Exit(1)`), so every host that calls it exits. A new host must call `AddServiceDefaults`. Check locally as PID 1: `unshare -fp --mount-proc dotnet Fleans.Api.dll` with an unreachable database.

## Aspire dev vs. publish topology

`Fleans.Aspire/Program.cs` registers `Fleans.WorkerHost` **only in publish mode**, so:

- `dotnet run --project Fleans.Aspire` keeps the dev 3-process topology (Api + Web + Redis).
- `aspire publish` emits api/web/worker/mcp + Redis (+ optional Postgres/Kafka).

`Aspire.Hosting.Kubernetes` ships preview-only — pinned to `13.6.5-preview.1.26559.7`; bump together with the rest of the Aspire 13.6.5 stack.

## Worker NuGet versioning

`Fleans.ExternalTasks.Sdk` shares the engine's `<VersionPrefix>` track — every release bumps it even if its source is bit-identical. The engine images bundle no custom-task plugin; ready-made plugins and their host images (e.g. `ghcr.io/nightbaker/fleans-rest-caller`) ship from [nightBaker/fleans-plugins](https://github.com/nightBaker/fleans-plugins).

## Compose-bundle post-processing

`aspire publish -t docker-compose` emits YAML that is structurally correct but unusable out of the box. The release pipeline runs `src/Fleans/scripts/postprocess-compose-bundle.sh` to fix it up. See [docs/runbooks/compose-bundle.md](../runbooks/compose-bundle.md) for the full list of fix-ups and the rule for adding new ones.
