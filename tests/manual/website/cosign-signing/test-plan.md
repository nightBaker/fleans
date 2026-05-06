# Cosign keyless signing — release artifacts

Manual verification that the release pipeline (`.github/workflows/release.yml`) signs every published artifact with cosign keyless and that the documented `cosign verify` / `cosign verify-blob` recipes succeed.

## Prerequisites

- `gh` CLI authenticated as a maintainer of `nightBaker/fleans`.
- `cosign` v2.x installed locally.
- `jq` installed (for inspecting the verify output).
- A throwaway Sigstore identity is fine — the verify path uses public Rekor.

## Scenarios

1. **Dry-run signs images successfully.** Trigger `gh workflow run release.yml -f version=0.0.0-rc-test` and `gh run watch <run-id>`. The `images` matrix job logs lines containing `Successfully verified SCT receipt` and `tlog entry created with index:` for **all 4** services (`api`, `web`, `worker`, `mcp`). No `secret not found` / `gpg-key not configured` errors. The `helm-package` job emits the same Rekor lines for the chart blob.

2. **Real tag produces signed images.** `git tag v0.1.0-beta && git push origin v0.1.0-beta`. After the workflow completes:

   ```bash
   cosign verify \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     ghcr.io/nightbaker/fleans-api:0.1.0-beta | jq
   ```

   Exits 0 and prints a JSON object with at least one `Bundle.tlogEntries[].logIndex`.

3. **Repeat for all 4 services.** All four `cosign verify` invocations succeed:

   ```bash
   for SVC in api web worker mcp; do
     cosign verify \
       --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
       --certificate-oidc-issuer https://token.actions.githubusercontent.com \
       ghcr.io/nightbaker/fleans-$SVC:0.1.0-beta
   done
   ```

   The release pipeline publishes 4 distinct signed images. The Helm chart's runtime topology — re-using `image.api` for both `core` and `worker` Deployments — is a separate concern; on the registry side every matrix-built image gets signed and verified independently.

4. **Verify the helm chart blob.** `gh release download v0.1.0-beta -p 'fleans-0.1.0-beta.tgz*'` (downloads `.tgz`, `.sig`, `.crt`); run the `cosign verify-blob` from `self-host-helm.md`:

   ```bash
   cosign verify-blob \
     --certificate fleans-0.1.0-beta.tgz.crt \
     --signature   fleans-0.1.0-beta.tgz.sig \
     --certificate-identity-regexp "https://github.com/nightBaker/fleans/.github/workflows/release.yml@refs/tags/v.*" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     fleans-0.1.0-beta.tgz
   ```

   Exits 0.

5. **Wrong identity is rejected (negative control).** Run:

   ```bash
   cosign verify \
     --certificate-identity-regexp "wrong" \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com \
     ghcr.io/nightbaker/fleans-api:0.1.0-beta
   ```

   Exits non-zero with a clear identity-mismatch error. Confirms the regex actually filters.

6. **Tampered chart is rejected.** Append a byte to a copy of the `.tgz` (`cp fleans-0.1.0-beta.tgz tampered.tgz && printf 'X' >> tampered.tgz`), run `cosign verify-blob` against the tampered file with the original `.sig` and `.crt`. Exits non-zero with a signature-mismatch error.

7. **No `cosign-installer` cache miss in CI.** Confirm via timing in the workflow run summary that the `Install cosign` step completes in ~2s.

## Cleanup after dry-run

Each `0.0.0-rc-test` invocation publishes 12 image versions per dispatch (4 services × { manifest list `0.0.0-rc-test`, per-RID `0.0.0-rc-test-x64`, per-RID `0.0.0-rc-test-arm64` }) and emits 4 immutable Rekor entries (manifest-list digests are what cosign signs). Use the `## Cutting a Release` rollback loop to clean up ghcr.io after each dry-run iteration:

```bash
for IMG in fleans-api fleans-web fleans-worker fleans-mcp; do
  for TAG in 0.0.0-rc-test 0.0.0-rc-test-x64 0.0.0-rc-test-arm64; do
    VID=$(gh api "/user/packages/container/$IMG/versions" \
      --jq '.[] | select(.metadata.container.tags | index("'"$TAG"'")) | .id' \
      | head -1)
    [ -n "$VID" ] && gh api -X DELETE "/user/packages/container/$IMG/versions/$VID"
  done
done
```

Rekor entries cannot be deleted — that's by design (transparency-log invariant). Repeated dry-runs leave orphan attestations linked to no current image, which is harmless but visible in `rekor-cli search` for the workflow's identity.

## Drift-guard pins

These line-pinned references back the CLAUDE.md regression entry. If any pin no longer resolves to the named symbol at the current branch SHA, update the test-plan and the regression entry together.

- `release.yml:173-185` — `Resolve image digest` → `Install cosign` → `Sign container image (keyless)` block inside the `images` matrix job, sitting after `Assemble multi-arch manifest list` so the digest captured is the manifest-list digest.
- `release.yml:273-292` — `Install cosign` + `Sign helm chart tarball (keyless blob)` block in the `helm-package` job (formerly `helm-drift`; renamed when the deep-diff was demoted to a lint), plus the `actions/upload-artifact` `path:` glob that picks up `*.tgz.sig` / `*.tgz.crt`.
- `release.yml:337-338` — `gh release create` asset list inside the `release` job (`./artifacts/*.sig` and `./artifacts/*.crt`).

## Reporting

Use the `PASSED` / `FAILED` / `BUG` / `KNOWN BUG` convention from the website regression list in `CLAUDE.md`.
