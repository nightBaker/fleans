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

### 8. Package signing + verification

Author-signing runs only when `NUGET_SIGNING_CERT_PFX_BASE64` is set on the
`nuget-publish` environment and `tools/signing/fleans-signing-cert.cer` is
committed (setup: [docs/runbooks/nuget-signing-rotation.md](../../../docs/runbooks/nuget-signing-rotation.md)).

**8a. Local sign + verify (no secrets, no network)** — validates the chain
end-to-end with a throwaway cert:

```bash
TMP=$(mktemp -d)
# Throwaway code-signing cert (codeSigning EKU is mandatory)
openssl req -x509 -newkey rsa:4096 -sha256 -days 30 -nodes \
  -keyout "$TMP/k.key" -out "$TMP/c.crt" \
  -subj "/CN=fleans-local-test" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=critical,codeSigning"
openssl pkcs12 -export -out "$TMP/s.pfx" -inkey "$TMP/k.key" -in "$TMP/c.crt" -passout pass:tp
FP=$(openssl x509 -in "$TMP/c.crt" -noout -fingerprint -sha256 | sed 's/.*=//; s/://g')

cd src/Fleans
dotnet pack Fleans.Worker/Fleans.Worker.csproj -c Release /p:Version=0.0.0-sign -o "$TMP/n"
dotnet nuget sign "$TMP"/n/*.nupkg "$TMP"/n/*.snupkg \
  --certificate-path "$TMP/s.pfx" --certificate-password tp \
  --timestamper http://timestamp.digicert.com

CFG="$TMP/nuget.config"
printf '<?xml version="1.0"?>\n<configuration>\n</configuration>\n' > "$CFG"
dotnet nuget trust certificate t "$FP" --algorithm SHA256 --allow-untrusted-root --configfile "$CFG"
dotnet nuget verify "$TMP"/n/*.nupkg --all --configfile "$CFG"
```

Expect: `dotnet nuget sign` succeeds (an `NU3018 UntrustedRoot` *warning* is
normal for a self-signed cert); `dotnet nuget verify --all` exits 0. As a
negative check, verifying an **unsigned** package with the same config fails
with `NU3004`, and verifying with a *wrong* trusted fingerprint fails with
`NU3034` — confirming the CI verify step cannot pass a silently-unsigned bundle.

**8b. CI dry-run signs (secrets set)** — re-run Step 2's `0.0.0-ci-test`
dispatch *after* the signing secret is configured. The "Sign packages" and
"Verify package signatures" steps run (not skipped); download the
`nuget-packages-0.0.0-ci-test` artifact and confirm each `.nupkg` carries an
author signature:

```bash
dotnet nuget verify <downloaded>.nupkg --certificate-fingerprint <published-FP>
```

(Self-signed: the `UntrustedRoot` notice is expected; the command still
confirms the signer matches the published fingerprint.)

## Expected outcomes checklist

- [ ] Local `dotnet pack` produces 3× `.nupkg` + 3× `.snupkg` with README bundled.
- [ ] `workflow_dispatch` dry-run with `version=0.0.0-ci-test` uploads artifacts but does NOT push to nuget.org.
- [ ] `gh release create v<VERSION>` triggers the workflow and publishes all three packages within ~10 min.
- [ ] Each package page on nuget.org shows README, MIT license, and `nightBaker/fleans` repo URL.
- [ ] Re-running the workflow on the same release is idempotent (`--skip-duplicate`).
- [ ] A clean external project successfully `dotnet add package`s and builds against all three.
- [ ] `.snupkg` symbols are reachable on the nuget.org symbol server (HTTP 200 / IDE source-stepping).
- [ ] Two consecutive `dotnet pack` runs at the same version produce bit-identical `.nupkg` files.
- [ ] Local sign + trust-by-fingerprint + `dotnet nuget verify --all` round-trips (Step 8a); unsigned → NU3004, wrong fingerprint → NU3034.
- [ ] With the signing secret set, the `0.0.0-ci-test` dry-run runs the Sign + Verify steps and the artifact packages carry an author signature matching the published fingerprint (Step 8b).

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
