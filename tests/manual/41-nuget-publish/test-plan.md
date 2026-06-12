# Manual Test Plan 41 — NuGet Publish on Release

## Scenario

Verify that `.github/workflows/nuget-publish.yml` packages and pushes the three
publishable Fleans plugin packages — `Fleans.Application.Abstractions`,
`Fleans.Worker`, and `Fleans.Plugins.RestCaller` — to nuget.org when a maintainer
publishes a GitHub Release. Also covers the `workflow_dispatch` dry-run path,
idempotency, external-consumer install, and `.snupkg` symbol availability.

This is a release-infrastructure plan. There is no BPMN fixture — the artifact
under test is the GitHub Actions workflow file itself.

## Prerequisites

- Maintainer is on `nightBaker/fleans` with rights to publish a Release.
- The `nuget-publish` environment exists in the repo (Settings → Environments).
- Secret `NUGET_API_KEY` is set on that environment, scoped `Fleans.*` and with
  the **Push new packages and package versions** permission. Configured via:
  ```bash
  gh secret set NUGET_API_KEY \
    --repo nightBaker/fleans \
    --env nuget-publish \
    --body '<paste-key-here>'
  ```
- Local `dotnet --version` reports a 10.0.x SDK (matches `actions/setup-dotnet`).
- Local clean checkout of `main` at the SHA you intend to tag.

## Steps

### 1. Local pack smoke (no network)

From `src/Fleans/`:

```bash
rm -rf /tmp/nupkgs
dotnet pack Fleans.Application.Abstractions/Fleans.Application.Abstractions.csproj \
  -c Release /p:Version=0.0.0-local -o /tmp/nupkgs
dotnet pack Fleans.Worker/Fleans.Worker.csproj \
  -c Release /p:Version=0.0.0-local -o /tmp/nupkgs
dotnet pack Fleans.Plugins.RestCaller/Fleans.Plugins.RestCaller.csproj \
  -c Release /p:Version=0.0.0-local -o /tmp/nupkgs
ls /tmp/nupkgs
```

Expect: 3× `.nupkg` and 3× `.snupkg`, each at version `0.0.0-local`.

Spot-check the README is bundled inside the package and the version is correct:

```bash
unzip -l /tmp/nupkgs/Fleans.Worker.0.0.0-local.nupkg | grep -E '(README|nuspec)'
```

Expect: `README.md` and `Fleans.Worker.nuspec` in the listing.

### 2. `workflow_dispatch` dry-run (no push)

From the GitHub UI: Actions → `nuget-publish` → "Run workflow" → set
`version` to `0.0.0-ci-test` → Run workflow.

Expect:
- The job runs to completion in ~3 min.
- The "Verify NUGET_API_KEY is set" step is **skipped** (the dry-run gate).
- The "Push packages + symbols to nuget.org" step is **skipped**.
- The `nuget-packages-0.0.0-ci-test` artifact is uploaded (3× `.nupkg` + 3× `.snupkg`).
- nuget.org has no new package version (verify by browsing
  https://www.nuget.org/packages/Fleans.Application.Abstractions/).

### 3. Real release flow

Maintainer cuts a tag and publishes:

```bash
# Bump <VersionPrefix> in src/Fleans/Directory.Build.props if needed, commit, merge.
gh release create v0.1.0-beta --generate-notes --repo nightBaker/fleans
```

Expect:
- The `release.published` event triggers the `nuget-publish` workflow.
- The workflow run is green within ~10 minutes.
- All three packages appear at:
  - https://www.nuget.org/packages/Fleans.Application.Abstractions/0.1.0-beta
  - https://www.nuget.org/packages/Fleans.Worker/0.1.0-beta
  - https://www.nuget.org/packages/Fleans.Plugins.RestCaller/0.1.0-beta
- Each package page shows: README rendered, License = MIT, repo URL pointing to
  `nightBaker/fleans`.

### 4. Idempotent re-run

Re-trigger the same release manually (Actions → `nuget-publish` → re-run failed
or all jobs).

Expect:
- The `dotnet nuget push --skip-duplicate` step succeeds with messages like
  `"already exists at the feed"` for each package; the job goes green; no new
  versions are created on nuget.org.

### 5. External-consumer install

In a clean directory outside the repo:

```bash
dotnet new classlib -o nuget-consumer-smoke
cd nuget-consumer-smoke
dotnet add package Fleans.Application.Abstractions --version 0.1.0-beta
dotnet add package Fleans.Worker --version 0.1.0-beta
dotnet add package Fleans.Plugins.RestCaller --version 0.1.0-beta
dotnet build
```

Expect: `dotnet build` succeeds with `Build succeeded.` and zero errors.

### 5b. Repository-signature verification (post-publish only)

nuget.org repository-signs every accepted package **server-side**, so this signature
exists only on a package restored from nuget.org — **not** on a dry-run or locally-packed
`.nupkg`. Run this **only against a real published version** (skip it for the
`0.0.0-ci-test` dry-run, where `verify` would report `NU3004` because nothing was pushed).

From the same `nuget-consumer-smoke` restore:

```bash
dotnet nuget verify --all \
  ~/.nuget/packages/fleans.worker/0.1.0-beta/fleans.worker.0.1.0-beta.nupkg
```

Expect: exit `0` with a valid **repository** signature reported (a `NU3004` here means
the package is unsigned/tampered, not repo-signed). Then confirm the consumer docs match:
the *Package integrity & signatures* section in
`website/src/content/docs/reference/self-hosting.md` describes this `verify --all` check,
the repo-vs-publisher-signature distinction, and the pre-1.0 trade-off — verify the text
still matches observed behavior.

### 6. Symbols verification

From the same `nuget-consumer-smoke` project (or any IDE configured to load
symbols from https://symbols.nuget.org):

- The `.snupkg` symbols are available at the nuget.org symbol server. Verify
  one of:
  - Visual Studio / Rider: open a debugger session, step into a `Fleans.Worker`
    type, and confirm source / line-number stepping works (SourceLink hits the
    GitHub commit at the tag SHA).
  - Or via API:
    ```bash
    curl -I "https://globalcdn.nuget.org/symbol-packages/fleans.worker.0.1.0-beta.snupkg"
    ```
    Expect HTTP 200.

### 7. Reproducibility smoke check

On a second clean clone (or after `git clean -fdx`):

```bash
cd src/Fleans
rm -rf artifacts1 artifacts/nuget
dotnet pack Fleans.Worker/Fleans.Worker.csproj -c Release /p:Version=0.1.0-beta -o artifacts/nuget
mv artifacts/nuget artifacts1
dotnet pack Fleans.Worker/Fleans.Worker.csproj -c Release /p:Version=0.1.0-beta -o artifacts/nuget
diff -r artifacts1 artifacts/nuget
```

Expect: `diff` output is empty (bit-identical packages across two pack runs,
backed by `Deterministic=true` + `ContinuousIntegrationBuild=true` in
`Directory.Build.props`).

## Expected outcomes checklist

- [ ] Local `dotnet pack` produces 3× `.nupkg` + 3× `.snupkg` with README bundled.
- [ ] `workflow_dispatch` dry-run with `version=0.0.0-ci-test` uploads artifacts but does NOT push to nuget.org.
- [ ] `gh release create v<VERSION>` triggers the workflow and publishes all three packages within ~10 min.
- [ ] Each package page on nuget.org shows README, MIT license, and `nightBaker/fleans` repo URL.
- [ ] Re-running the workflow on the same release is idempotent (`--skip-duplicate`).
- [ ] A clean external project successfully `dotnet add package`s and builds against all three.
- [ ] (Post-publish) `dotnet nuget verify --all` on a restored package reports a valid nuget.org **repository** signature; the `self-hosting.md` *Package integrity & signatures* section matches observed behavior.
- [ ] `.snupkg` symbols are reachable on the nuget.org symbol server (HTTP 200 / IDE source-stepping).
- [ ] Two consecutive `dotnet pack` runs at the same version produce bit-identical `.nupkg` files.

## Notes

- The workflow file lives at `.github/workflows/nuget-publish.yml`.
- All NuGet metadata (license, repo URL, README, deterministic build flags) is
  configured in `src/Fleans/Directory.Build.props` and the three packable
  csproj files; the workflow itself only orchestrates pack + push.
- `NUGET_API_KEY` is environment-scoped (not repo-scoped) so future "required
  reviewer" gating on the `nuget-publish` environment can be enabled without
  YAML changes.
- Rotation cadence for `NUGET_API_KEY`: rotate 30 days before the 365-day
  expiry; tracked in the Release Runbook (#409).
