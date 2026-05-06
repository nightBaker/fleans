# Manual Test Plan — Release pipeline `.github/workflows/release.yml` (Issue #408)

End-to-end validation that a `git push origin v<SemVer>` produces:
- 4 multi-arch (`linux/amd64` + `linux/arm64`) container images on `ghcr.io/nightbaker/fleans-{api,web,worker,mcp}:v<VERSION>`.
- A `:latest` tag movement only when the version is non-prerelease.
- A docker-compose zip + Helm chart tgz attached to the GitHub Release.
- A clean `helm lint` + smoke render of the chart (with `worker.enabled=true` and `customWorker.enabled=true`).
- `release.published` triggering `nuget-publish.yml` for the plugin packages.

## Prerequisites

- `gh` CLI authenticated as a `nightBaker/fleans` maintainer with `release` environment access.
- Docker available locally for `dotnet publish /t:PublishContainer` smoke (scenario 1).
- Write access to `ghcr.io/nightbaker/*` packages.
- Empty/clean local checkout of `feature/408-release-pipeline` (or post-merge `main`).

## Scenarios

### 1. Local pack smoke (developer-box, no GitHub Actions)

For each of the 4 services (api/web/worker/mcp), run a single-arch publish to the local Docker daemon:

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj \
  /t:PublishContainer \
  /p:Version=0.0.0-rc-test \
  /p:RuntimeIdentifier=linux-x64
docker image inspect fleans-api:0.0.0-rc-test
```

**Expect:** `dotnet publish` exits 0 and `docker image inspect` returns a non-empty result. Multi-arch manifest assembly is exercised in CI only (Scenario 2) — locally that requires QEMU + buildx and isn't worth the per-machine setup for a smoke test.

Repeat for `Fleans.Web`, `Fleans.WorkerHost`, `Fleans.Mcp`.

### 2. Dispatch dry-run (no real release)

```bash
gh workflow run release.yml --ref feature/408-release-pipeline -f version=0.0.0-rc-test
gh run watch
```

**Expect:**
- (a) All 4 image legs of the `images` matrix succeed.
- (b) The `compose` job succeeds and `docker-compose-v0.0.0-rc-test.zip` is uploaded as a workflow artifact.
- (c) The `helm-package` job succeeds: `helm lint charts/fleans` exits 0, the `--set worker.enabled=true --set customWorker.enabled=true` smoke render produces non-empty output, `helm package` writes `/tmp/fleans-0.0.0-rc-test.tgz`, and `cosign sign-blob` emits `tlog entry created with index:`.
- (d) The `release` job is **skipped** (`if: needs.setup.outputs.is_dispatch_dry_run != 'true' && github.event_name == 'push'`). Verify with `gh run view --log` that the release step says "skipped".
- (e) No `v0.0.0-rc-test` tag or GitHub Release is created on the repo.

### 3. Real release on a sandbox tag

```bash
git tag v0.0.1-rc-test
git push origin v0.0.1-rc-test
gh run watch
```

**Expect:**
- (i) All 4 images appear under `https://github.com/nightBaker/fleans/pkgs/container/fleans-{api,web,worker,mcp}`, each with `linux/amd64` + `linux/arm64` in the manifest list. Verify via `docker buildx imagetools inspect ghcr.io/nightbaker/fleans-api:0.0.1-rc-test`.
- (ii) A GitHub Release titled `v0.0.1-rc-test` is created, marked **prerelease** (because the version contains `-rc-test`), with auto-generated notes grouped per the `.github/release.yml` categorizer.
- (iii) The release has `docker-compose-v0.0.1-rc-test.zip` and `fleans-0.0.1-rc-test.tgz` attached.
- (iv) `release.published` triggers a run of `nuget-publish.yml` automatically (the existing release-published trigger fires).

### 4. `:latest` rule

- Verify that scenario 3's prerelease tag did **NOT** move `:latest` for any of the 4 services. Inspect `ghcr.io/nightbaker/fleans-api:latest` — it should still point to whatever it pointed to before the rehearsal (or be absent if no prior stable release).
- (Hypothetical) push `v9.9.9` (a non-prerelease tag) and verify `:latest` is moved. Skip if no `v9.9.9` rehearsal is desired; the `is_prerelease` boolean in `setup`'s output makes the rule deterministic.

### 5. Idempotent re-run

Re-run the workflow on the same tag:

```bash
gh run rerun --workflow release.yml
```

**Expect:**
- The `images` legs succeed (idempotent push to ghcr.io — same digest).
- The `release` job fails with `release v0.0.1-rc-test already exists` because `gh release create` errors on duplicate. **This is intentional** — the maintainer chooses `gh release delete v0.0.1-rc-test --cleanup-tag` and re-runs, or uses `gh release upload --clobber` for asset-only updates. No silent overwrites.

### 6. Helm chart consumability

```bash
gh release download v0.0.1-rc-test -p '*.tgz' -D /tmp
helm install fleans /tmp/fleans-0.0.1-rc-test.tgz --dry-run
```

**Expect:** `helm install --dry-run` succeeds; no template errors. (A real install requires a sandbox cluster — out of scope for this scenario; covered by `tests/manual/website/...`-style infra tests if needed.)

### 7. Cleanup

```bash
gh release delete v0.0.1-rc-test --cleanup-tag --yes
# Manually delete the 4 ghcr.io packages' rc-test tags from the package settings UI.
```

**Expect:** the next real `0.1.0-beta` rehearsal starts from a clean slate.

### 8. Helm-template regression (regression-guard)

Branch off `main`. In `charts/fleans/templates/deployment-core.yaml`, introduce a deliberate template error (e.g., `{{ .Values.notARealField | required "deliberate break" }}` or unbalanced `{{- if }}`). Push the branch and dispatch the workflow:

```bash
gh workflow run release.yml --ref feature/helm-template-trip-wire -f version=0.0.0-rc-test
gh run watch
```

**Expect:** the `helm-package` job **fails** at the `Lint helm chart` step (or, for required-value breaks, at `Smoke render with all components enabled`) with the exact line and template name. Revert the break, re-run, confirm the job passes again.

This is a lint-shaped regression-guard — not a drift comparison. The prior "deep diff" design (Aspire-published k8s YAML diffed against the rendered chart) was abandoned: the chart is hand-written for end-user ergonomics (single Deployment+Service per service, inline env vars), while the Aspire K8s publisher emits per-service `<name>-deployment` / `<name>-service` / `<name>-config` (ConfigMap) / `<name>-secrets` shapes. The two were never the same shape; the deep-diff was checking an alignment that did not exist.

## Pitfalls

Seven issues hit early dispatches and were fixed before any tag shipped — keep them in mind when editing `release.yml` or `Fleans.Aspire/Program.cs`:

1. **Aspire CLI is a dotnet tool, not a workload.** Aspire 9+/13.x ships the CLI as the `Aspire.Cli` global tool; the .NET 8/Aspire 8 era `dotnet workload install aspire` is a no-op for CLI installation now. Use `dotnet tool install -g Aspire.Cli --prerelease` and prepend `$HOME/.dotnet/tools` to `$GITHUB_PATH`. Applies to the `compose` job (the `helm-package` job no longer runs `aspire publish`).
2. **`Aspire.AppHost.Sdk` must match the Aspire.Hosting.* package version.** Aspire CLI 13.x rejects an apphost whose `<Sdk Name="Aspire.AppHost.Sdk" Version="…">` does not match the host packages (`The app host is not compatible. Aspire.Hosting version: 9.0.0`, exit code 9). Bump the SDK pin in `Fleans.Aspire.csproj` whenever the `Aspire.Hosting.*` packages move; today both should be `13.2.3`. The dev-mode `dotnet run --project Fleans.Aspire` does NOT surface this — only `aspire publish` validates the SDK pin.
3. **Semicolons in MSBuild property values must be `%3B`-escaped.** `dotnet publish /p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64"` fails on Linux (`MSB1006: Property is not valid. Switch: linux-arm64`) because MSBuild's CLI parser splits on `;`. Use `/p:ContainerRuntimeIdentifiers=linux-x64%3Blinux-arm64` (URL-escaped) — works in bash, in `run: |` blocks, and in zsh.
4. **`ContainerRuntimeIdentifiers` (plural) breaks scalar `OutputPath` targets.** Even with the `%3B` escape, .NET 10 SDK 10.0.203 expands the multi-RID list into `$(OutputPath)` so a downstream MSBuild target (`HasTrailingSlash`) trips with `MSB4115: only accepts a scalar value`. Use the canonical multi-arch pattern instead: publish each RID separately at a per-RID image tag (`/p:RuntimeIdentifier=linux-x64 /p:ContainerImageTag=$VERSION-x64`) and assemble the manifest list with `docker buildx imagetools create --tag $REPO:$VERSION $REPO:$VERSION-x64 $REPO:$VERSION-arm64`. Cosign signs the manifest-list digest.
5. **Both publishers' `BeforeStart` hooks run regardless of `-t` target.** Referencing both `Aspire.Hosting.Docker` and `Aspire.Hosting.Kubernetes` causes both to subscribe their hooks at apphost startup — so `aspire publish -t docker-compose` still runs the K8s publisher's resource generation, and any K8s-incompatible primitive (e.g. `WithBindMount`) crashes the run with `Bind mounts are not supported by the Kubernetes publisher` even when the user asked for compose. Conversely, the load-test nginx fan-out (`tests/load/nginx.conf` bind-mounted into `nginx:1.27`) is a developer-only concern that doesn't belong in release artifacts. Gate it behind an opt-in env var (`FLEANS_LOAD_TEST_MODE=true`) read via `builder.Configuration["FLEANS_LOAD_TEST_MODE"]`. The load-test runbook (`tests/load/README.md`) flips the flag; the release pipeline leaves it off.
6. **Aspire-published Deployments need a matching helm template.** Even after demoting the deep-diff to a lint, every deployable (e.g. `fleans-core`, `fleans-worker`, `fleans-custom-worker`) needs a `charts/fleans/templates/deployment-<name>.yaml` so end-users actually get it on `helm install`. New Aspire deployables must ship with the matching chart template AND a values block. Default the values' `enabled` flag to `false` (small/single-node installs use Combined silos), and update the `helm-package` job's smoke-render `--set <name>.enabled=true` list so lint/render exercises the template.
7. **Aspire's K8s publisher and the helm chart are intentionally different shapes.** The Aspire 13.x `Aspire.Hosting.Kubernetes` publisher emits per-service `<name>-deployment`, `<name>-service`, `<name>-config` (ConfigMap), and `<name>-secrets` (Secret) — verbose, env-vars externalized. The helm chart uses single-resource-per-service naming (`<name>` Deployment + Service) with inline `env:` blocks. They were never the same shape; the original `helm-drift` deep-diff was checking an alignment that did not exist and would always fail. Keep the helm path as `helm lint` + `helm template` smoke render; do not re-introduce the deep-diff.

## Verdict

- **PASSED** — all 8 scenarios green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
