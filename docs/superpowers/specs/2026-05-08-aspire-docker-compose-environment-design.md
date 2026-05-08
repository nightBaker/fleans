# Wire up `AddDockerComposeEnvironment` so the release pipeline ships a real docker-compose bundle

## Problem

`.github/workflows/release.yml`'s `compose` job runs `aspire publish --project Fleans.Aspire -t docker-compose -o out/compose` and zips the output as `docker-compose-v<VERSION>.zip`. Inspection of the artifact from the most recent green dry-run (run #25451652427) shows the zip contains an **Aspire-auto-generated Helm chart** (`Chart.yaml`, `values.yaml`, `templates/<svc>/{deployment,service,config,secrets}.yaml`), **not** a docker-compose YAML.

Root cause: `Fleans.Aspire/Program.cs` registers `builder.AddKubernetesEnvironment("k8s")` but never calls `AddDockerComposeEnvironment(...)`. With no Compose environment registered, `aspire publish -t docker-compose` silently routes to the only registered environment (Kubernetes) and `Aspire.Hosting.Kubernetes 13.x` emits a Helm chart into `out/compose/`. The zip step happily packs whatever it finds.

End-user impact: anyone following `website/src/content/docs/guides/self-host-docker-compose.md` downloads `docker-compose-v<VERSION>.zip`, finds no `compose.yaml`, and the documented `docker compose up` install path is broken.

## Decision

Adopt **Option 1 + B** from brainstorming:

1. **Drop Aspire's auto-generated Helm chart as a release artifact.** It was an accidental side-effect, not an intentional deliverable. The hand-written `charts/fleans/` chart (packaged by the `helm-package` job into `fleans-<VERSION>.tgz`) is the supported install path; it has all the operator-tunable knobs (`core.replicas`, `worker.enabled`, `ingress.host`, etc.) that Aspire's auto-emit cannot express.
2. **Make the `compose` job actually produce a docker-compose bundle** by registering a Compose environment in the apphost and asserting the file lands before zipping.

Rejected alternatives:

- **Drop the docker-compose deliverable entirely.** Possible (Helm covers most install paths), but the existing `self-host-docker-compose.md` guide and the test plan #15 ("Self-host runbook") document it as a first-class install path. Removing the bundle would force a docs rewrite and would deny end-users who don't run K8s.
- **Customize the Aspire-emitted Helm chart to match the hand-written one.** Aspire 13.x's customization surface (`WithHelm()`, `WithContainerRegistry()` (preview), `PublishAsKubernetesService()`) does not expose `values.yaml`-tunable replicas/resources/scheduling, has no Ingress generation, and offers no `_helpers.tpl` / `NOTES.txt` injection. Closing the gap would require a brittle post-processing callback that breaks every time Aspire's output format changes.

## Architecture

### `src/Fleans/Fleans.Aspire/Program.cs`

> **Spec amendment (post-implementation):** The original spec proposed registering BOTH `AddKubernetesEnvironment("k8s")` AND `AddDockerComposeEnvironment("compose")` simultaneously. Implementation uncovered that Aspire 13.2.3 does not support dual-registration without every resource calling `WithComputeEnvironment(...)` to disambiguate, which is impractical for a multi-resource apphost. The implemented design is a runtime switch instead. The user-visible outcome is equivalent: `aspire publish -t docker-compose` produces a real `compose.yaml`, and `aspire publish -t kubernetes` is still reachable (under an explicit env-var gesture) for ad-hoc dev usage.

Replace the unconditional `AddKubernetesEnvironment("k8s")` call with a runtime switch on the `ASPIRE_PUBLISH_ENV` configuration value (default `"compose"`):

```csharp
var publishEnv = builder.Configuration["ASPIRE_PUBLISH_ENV"] ?? "compose";
if (publishEnv.Equals("compose", StringComparison.OrdinalIgnoreCase))
{
    builder.AddDockerComposeEnvironment("compose");
}
else if (publishEnv.Equals("kubernetes", StringComparison.OrdinalIgnoreCase))
{
    builder.AddKubernetesEnvironment("k8s");
}
else
{
    throw new InvalidOperationException(
        $"Unknown ASPIRE_PUBLISH_ENV value '{publishEnv}'. Valid values: 'compose' (default), 'kubernetes'.");
}
```

Default behaviour:
- `aspire publish -t docker-compose` (release pipeline path) → registers Compose env → real `compose.yaml`.
- `ASPIRE_PUBLISH_ENV=kubernetes aspire publish -t kubernetes` (ad-hoc dev path) → registers K8s env → Aspire-flavored Helm chart (currently unused as a release artifact; that is fine).
- Dev mode (`dotnet run --project Fleans.Aspire`) unchanged — both extension methods are publish-only no-ops in non-publish execution contexts.
- Unknown values fail fast with `InvalidOperationException`, matching the project's fail-fast convention for `FLEANS_PERSISTENCE_PROVIDER` / `FLEANS_STREAMING_PROVIDER`.

The existing `FLEANS_LOAD_TEST_MODE` gate around the nginx + 2-replica fan-out stays as-is. Reason for the guard under the runtime-switch model: keep the load-test nginx out of end-user release artifacts (a developer concern, not an end-user concern), and protect the rare `ASPIRE_PUBLISH_ENV=kubernetes` path from the K8s publisher's bind-mount rejection.

### `.github/workflows/release.yml`

The `compose` job's "Zip the compose bundle" step gains a fail-loud assertion before zipping:

```bash
[ -f out/compose/compose.yaml ] || [ -f out/compose/docker-compose.yaml ] || \
  { echo "::error::aspire publish -t docker-compose produced no compose file in out/compose/"; ls -la out/compose/; exit 1; }
```

(Aspire 13.x emits `compose.yaml`; the `docker-compose.yaml` fallback is a hedge against future format renames.) No other changes to the job — same zip name, same artifact upload, same downstream consumption by the `release` job.

`helm-package` job is unchanged — it remains the only chart producer.

## Test plan

### Local verification (preferred)

```bash
cd src/Fleans
dotnet tool install -g Aspire.Cli --prerelease   # one-shot
aspire publish --project Fleans.Aspire -t docker-compose -o /tmp/aspire-compose
ls -la /tmp/aspire-compose/
docker compose -f /tmp/aspire-compose/compose.yaml config | head -40
```

Expect:

- `/tmp/aspire-compose/compose.yaml` exists.
- `docker compose ... config` parses without errors.
- The rendered config references `ghcr.io/nightbaker/fleans-{api,web,worker,mcp,custom-worker}` services with the expected env vars and Redis dependency.

### CI verification (fallback)

Push as a PR, run the merge-then-dispatch loop (per the recent fix series — auto-merge to main, then `gh workflow run release.yml --ref main -f version=0.0.0-rc-test`). Download `docker-compose-v0.0.0-rc-test.zip` from the run artifacts and confirm:

- The zip contains `compose.yaml` (or `docker-compose.yaml`) at the root, NOT `Chart.yaml` + `templates/`.
- All 5 services (api/web/worker/mcp/custom-worker) appear with image refs `ghcr.io/nightbaker/fleans-<svc>:0.0.0-rc-test`.
- Redis (and Postgres if FLEANS_PERSISTENCE_PROVIDER were Postgres) appear as compose services.
- The pre-zip assertion never fires (would surface as a workflow failure with the `::error::` annotation).

### Documentation updates

- `tests/manual/42-release-pipeline/test-plan.md` Pitfalls section gains a new entry:
  > **Both `AddKubernetesEnvironment(...)` and `AddDockerComposeEnvironment(...)` must be registered if `release.yml` invokes both `-t kubernetes` and `-t docker-compose`.** Without the matching `AddXxxEnvironment` call, Aspire silently routes the publish to whatever environment IS registered (e.g., Kubernetes → produces Helm-shaped output instead of Compose YAML) and the artifact is unusable. The pre-zip assertion in the `compose` job catches the regression.
- Scenario 2 expected outcome gains: "`out/compose/compose.yaml` (or `docker-compose.yaml`) exists and is valid Compose Spec — pre-zip assertion catches missing files."

No website docs change required: `self-host-docker-compose.md` already documents the `docker compose up` install path; once the bundle is real, the guide just works.

## Out of scope

- Customizing Aspire's K8s publisher output to match the hand-written chart (would require a brittle post-processing callback; rejected above).
- Removing the K8s publisher registration entirely. The hand-written chart is the supported K8s install path, but `AddKubernetesEnvironment("k8s")` is still useful as a dev-time scaffold to eyeball "what env vars does this service need" when extending the apphost.
- Migrating the load-test command in `tests/load/README.md`. With a real Compose env registered, `FLEANS_LOAD_TEST_MODE=true dotnet run --project Fleans.Aspire -- --publisher docker-compose --output-path tests/load/generated` produces a real compose stack — the README command works as documented without further changes.
