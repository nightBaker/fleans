# Manual Test Plan — Release pipeline `.github/workflows/release.yml` (Issue #408)

End-to-end validation that a `git push origin v<SemVer>` produces:
- 4 multi-arch (`linux/amd64` + `linux/arm64`) container images on `ghcr.io/nightbaker/fleans-{api,web,worker,mcp}:v<VERSION>`.
- A `:latest` tag movement only when the version is non-prerelease.
- A docker-compose zip + Helm chart tgz attached to the GitHub Release.
- A green deep-diff between Aspire-published k8s output and the rendered Helm chart.
- `release.published` triggering `nuget-publish.yml` for the plugin packages.

## Prerequisites

- `gh` CLI authenticated as a `nightBaker/fleans` maintainer with `release` environment access.
- Docker available locally for `dotnet publish /t:PublishContainer` smoke (scenario 1).
- Write access to `ghcr.io/nightbaker/*` packages.
- Empty/clean local checkout of `feature/408-release-pipeline` (or post-merge `main`).

## Scenarios

### 1. Local pack smoke (developer-box, no GitHub Actions)

For each of the 4 services (api/web/worker/mcp):

```bash
cd src/Fleans
dotnet publish Fleans.Api/Fleans.Api.csproj \
  /t:PublishContainer \
  /p:Version=0.0.0-rc-test \
  /p:ContainerRuntimeIdentifiers=linux-x64%3Blinux-arm64
docker buildx imagetools inspect fleans-api:0.0.0-rc-test
```

**Expect:** the manifest list shows both `linux/amd64` and `linux/arm64` digests.

Repeat for `Fleans.Web`, `Fleans.WorkerHost`, `Fleans.Mcp`.

### 2. Dispatch dry-run (no real release)

```bash
gh workflow run release.yml --ref feature/408-release-pipeline -f version=0.0.0-rc-test
gh run watch
```

**Expect:**
- (a) All 4 image legs of the `images` matrix succeed.
- (b) The `compose` job succeeds and `docker-compose-v0.0.0-rc-test.zip` is uploaded as a workflow artifact.
- (c) The `helm-drift` job succeeds with `Drift check passed.` in the log.
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

### 8. Drift-detection trip wire (regression-guard)

Branch off `main`. In `Fleans.Aspire/Program.cs`, rename one env-var passed to `Fleans.Api` (e.g., `Fleans__Persistence__Provider` → `Fleans__Persistence__Mode`). Push the branch and dispatch the workflow:

```bash
gh workflow run release.yml --ref feature/drift-trip-wire -f version=0.0.0-rc-test
gh run watch
```

**Expect:** the `helm-drift` job **fails** with a `::error::` message naming the exact resource path (e.g., `Drift: Deployment/fleans-api differs between Aspire and Helm`). The diff in the job log shows the env-var rename. Revert the rename, re-run, confirm the job passes again.

This validates the deep-diff algorithm catches the failure mode that motivated the maintainer's "deep diff" decision.

## Pitfalls

Two issues hit the first dispatch (run #25436562303) and were fixed before any tag shipped — keep them in mind when editing `release.yml`:

1. **Aspire CLI is a dotnet tool, not a workload.** Aspire 9+/13.x ships the CLI as the `Aspire.Cli` global tool; the .NET 8/Aspire 8 era `dotnet workload install aspire` is a no-op for CLI installation now. Use `dotnet tool install -g Aspire.Cli --prerelease` and prepend `$HOME/.dotnet/tools` to `$GITHUB_PATH`. Applies to both the `compose` and `helm-drift` jobs.
2. **Semicolons in MSBuild property values must be `%3B`-escaped.** `dotnet publish /p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64"` fails on Linux (`MSB1006: Property is not valid. Switch: linux-arm64`) because MSBuild's CLI parser splits on `;`. Use `/p:ContainerRuntimeIdentifiers=linux-x64%3Blinux-arm64` (URL-escaped) — works in bash, in `run: |` blocks, and in zsh.

## Verdict

- **PASSED** — all 8 scenarios green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
