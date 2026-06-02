# Cutting a release

The release pipeline at `.github/workflows/release.yml` triggers on `git push origin v<SemVer>`. This is the maintainer runbook. Manual test plan: `tests/manual/42-release-pipeline/test-plan.md`.

## Pre-tag checklist

1. **Manual regression suite green** — run the full BPMN regression list (`tests/manual/01-…/` through the latest entry) plus the website regression list against `main`. Document any KNOWN-BUG verdicts in the release notes draft. See [docs/conventions/regression-testing.md](../conventions/regression-testing.md).
1a. **Verify `Universley.OrleansContrib.StreamsProvider.Redis` pin** in `Fleans.ServiceDefaults.csproj` matches what the manual regression actually ran against. The package uses date-based versioning (`YYYY.M.D`) and is not SemVer; an unintentional version drift could ship a different Redis-streaming adapter than the one the regression validated.
2. **Version bump in `Directory.Build.props`** — `<VersionPrefix>` only needs a hand-bump for *local development builds* (so dev images get reasonable tags). The release workflow overrides `/p:Version=<git-tag-without-v>` regardless, so the assembly + container tag always match the git tag.
3. **Changelog draft** — start from `git log v<PREV>..main --oneline --no-merges`. The workflow auto-generates release notes via `gh release create --generate-notes`; a hand-authored "Highlights" section makes the post readable.
4. **Pre-release dry-runs — run BOTH workflows** (the sentinels are intentionally different):
   - **`release.yml` dry-run:** `gh workflow run release.yml -f version=0.0.0-rc-test`. Uploads compose-zip + helm-tgz artifacts but skips `gh release create` (gated on `is_dispatch_dry_run`). Download the artifacts and smoke-test compose + helm against a `kind` cluster.
   - **`nuget-publish.yml` dry-run:** `gh workflow run nuget-publish.yml -f version=0.0.0-ci-test`. Packs the 4 plugin packages and uploads them as workflow artifacts but skips the actual `dotnet nuget push` (gated by `inputs.version != '0.0.0-ci-test'` on push/pack steps).

## Tag command

```bash
git tag v0.1.0-beta && git push origin v0.1.0-beta
```

The release workflow runs setup → images → compose → helm-package → release in a single CI run. The same `v<SemVer>` tag push triggers `nuget-publish.yml` in parallel; that workflow waits for the GitHub Release object to exist before publishing to nuget.org, so a failed `release.yml` never produces orphan NuGet packages.

(We can't use `on.release.published` here: releases created by the default `GITHUB_TOKEN` do not trigger downstream workflows — GitHub anti-recursion safeguard.)

## Post-tag verification

1. **Workflow run green** — `gh run list --workflow=release.yml --limit 1` should show the tagged run as ✅ on every job.
2. **All 4 images pullable, multi-arch** — `docker buildx imagetools inspect ghcr.io/nightbaker/fleans-{api,web,worker,mcp}:0.1.0-beta` should resolve `linux/amd64` + `linux/arm64`.
3. **Release assets attached** — `gh release view v0.1.0-beta --json assets` should list `docker-compose-v0.1.0-beta.zip` + `fleans-0.1.0-beta.tgz`.
4. **Notes look right** — auto-generated notes group commits per `.github/release.yml` categories.
5. **NuGet publish triggered + green** — pushing the tag fires `nuget-publish.yml` in parallel with `release.yml`. It blocks on the GitHub Release existing (max 20 min wait) before pushing to nuget.org. Verify via `gh run list --workflow=nuget-publish.yml --limit 1`. Verify each of the 4 packages on nuget.org: `Fleans.Domain.Abstractions`, `Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller`.
6. **Cosign verify smoke test** — pick one of the published images and verify the signature against Sigstore. The output should include a `Bundle` block with a `tlogEntries[0].logIndex` proving the signature was logged to the public Rekor transparency log.

   ```bash
   cosign verify \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     ghcr.io/nightbaker/fleans-api:0.1.0-beta | jq
   ```

   For the helm chart, verify the blob signature using the `.sig` and `.crt` attached to the release (see `self-host-helm.md` for the exact command):

   ```bash
   gh release download v0.1.0-beta -p 'fleans-0.1.0-beta.tgz*'
   cosign verify-blob \
     --certificate fleans-0.1.0-beta.tgz.crt \
     --signature   fleans-0.1.0-beta.tgz.sig \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     fleans-0.1.0-beta.tgz
   ```

## Rollback

If a release ships broken:

```bash
# 1. Remove the GH release + tag (deleting the release deletes the remote tag too).
gh release delete v0.1.0-beta --cleanup-tag --yes

# Drop the local tag if still present.
git tag -d v0.1.0-beta 2>/dev/null || true

# 2. Delete the four ghcr.io image versions for the broken tag.
#    (`docker buildx imagetools` has no `remove` subcommand — use the GH Packages REST API.)
for IMG in fleans-api fleans-web fleans-worker fleans-mcp; do
  VID=$(gh api "/user/packages/container/$IMG/versions" \
    --jq '.[] | select(.metadata.container.tags | index("0.1.0-beta")) | .id' \
    | head -1)
  if [ -n "$VID" ]; then
    gh api -X DELETE "/user/packages/container/$IMG/versions/$VID"
    echo "Deleted $IMG version $VID (tag 0.1.0-beta)"
  else
    echo "No version of $IMG with tag 0.1.0-beta — skipping"
  fi
done
```

If the org name on ghcr.io ever changes from a user account to an organization, swap `/user/packages/...` for `/orgs/<org>/packages/...`. The token that runs this needs the `delete:packages` scope.

**NuGet packages cannot be deleted** from nuget.org — only unlisted (`dotnet nuget delete <package> <version> --source https://api.nuget.org/v3/index.json -k <KEY>`). If a broken plugin shipped, ship a hotfix release immediately rather than relying on unlisting.

## Documentation rule reminder

Every release that introduces user-visible changes MUST update the version-pinned guides in the same PR per the existing **Documentation rule**. These reference the *current* tag in download URLs/commands — bumping `v0.1.0-beta` → `v0.2.0` requires a docs sweep:

- `website/src/content/docs/guides/quick-start.mdx`
- `website/src/content/docs/guides/self-host-docker-compose.md`
- `website/src/content/docs/guides/self-host-helm.md`

`grep -rl 'v<previous-tag>' website/src/content/docs/guides` to catch any that drifted.
