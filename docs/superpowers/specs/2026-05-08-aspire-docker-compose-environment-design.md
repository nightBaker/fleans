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

Add one line near the top, alongside the existing `AddKubernetesEnvironment("k8s")`:

```csharp
builder.AddDockerComposeEnvironment("compose");
```

Both publishers stay registered. The apphost continues to work in dev mode (`dotnet run --project Fleans.Aspire`) unchanged — `AddDockerComposeEnvironment` is a publish-time-only registration. With both environments present:

- `aspire publish -t docker-compose` dispatches to the Compose publisher → real `compose.yaml`.
- `aspire publish -t kubernetes` continues to dispatch to the K8s publisher → Helm-shaped output (currently unused as a release artifact; that is fine).

The existing `FLEANS_LOAD_TEST_MODE` gate around the nginx + 2-replica fan-out stays as-is. The gate exists because the K8s publisher rejects bind mounts; the Compose publisher accepts them natively, but both publishers' `BeforeStart` hooks fire on every `aspire publish` regardless of `-t` target. So the gate is still required when load-testers run `FLEANS_LOAD_TEST_MODE=true ... -t kubernetes` (uncommon, but possible).

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
