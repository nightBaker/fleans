# Container builds

Container images are produced by the .NET SDK's built-in `SdkContainerSupport` — **no Dockerfiles**. Each deployable service has a `<ContainerRepository>fleans-<svc></ContainerRepository>` in its csproj; `src/Fleans/Directory.Build.props` is the single source of truth for `<VersionPrefix>` and `<ContainerImageTag>`.

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj /t:PublishContainer /p:Version=0.1.0-test
# Same for Fleans.Web, Fleans.WorkerHost, Fleans.Mcp.

aspire publish --project Fleans.Aspire -t docker-compose -o out/compose
aspire publish --project Fleans.Aspire -t kubernetes  -o out/k8s
```

## Aspire dev vs. publish topology

`Fleans.Aspire/Program.cs` registers `Fleans.WorkerHost` **only in publish mode**, so:

- `dotnet run --project Fleans.Aspire` keeps the dev 3-process topology (Api + Web + Redis).
- `aspire publish` emits api/web/worker/mcp + Redis (+ optional Postgres/Kafka).

`Aspire.Hosting.Kubernetes` ships preview-only — pinned to `13.2.3-preview.1.26217.6`; bump together with the rest of the Aspire 13.2.3 stack.

## Plugin NuGet versioning

Plugin NuGet packages (`Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller`) share the engine's `<VersionPrefix>` track — every release bumps every plugin even if its source is bit-identical.

## Compose-bundle post-processing

`aspire publish -t docker-compose` emits YAML that is structurally correct but unusable out of the box. The release pipeline runs `src/Fleans/scripts/postprocess-compose-bundle.sh` to fix it up. See [docs/runbooks/compose-bundle.md](../runbooks/compose-bundle.md) for the full list of fix-ups and the rule for adding new ones.
