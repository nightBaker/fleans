# NuGet signing certificate rotation

Fleans author-signs its four published plugin packages — `Fleans.Domain.Abstractions`,
`Fleans.Application.Abstractions`, `Fleans.Worker`, `Fleans.Plugins.RestCaller` — with a
**self-signed** code-signing certificate (issue #458). This runbook covers initial setup
and rotation.

The signing step lives in `.github/workflows/nuget-publish.yml` and runs only when the
`NUGET_SIGNING_CERT_PFX_BASE64` secret is set. When it is unset (forks, or before initial
setup) the workflow skips signing and still publishes — so the steps below are what *turns
signing on* and what *keeps it working* across cert expiry.

## When to rotate

- ~30 days before the certificate expires. `generate-cert.sh` issues a 3-year (1095-day)
  cert, so this is roughly a triennial task.
- Immediately on any suspected private-key compromise.

## One-time setup / rotation procedure

1. **Generate a new cert** (holds the private key locally; never commit it):

   ```bash
   tools/signing/generate-cert.sh '<strong-password>'
   ```

   This writes `tools/signing/fleans-signing-cert.cer` (public, DER) and a local
   `fleans-signing.pfx` + key, and prints the SHA-256 fingerprint.

2. **Commit the public certificate** — this is the trust anchor consumers pin against, and
   the CI verify step derives the expected fingerprint from it (single source of truth, no
   separate variable to keep in sync):

   ```bash
   git add tools/signing/fleans-signing-cert.cer
   git commit -m "chore(signing): rotate NuGet signing certificate (#458)"
   ```

3. **Set the two repo secrets** with the private material (portable base64 — works on macOS
   and Linux):

   ```bash
   base64 < fleans-signing.pfx | tr -d '\n' \
     | gh secret set NUGET_SIGNING_CERT_PFX_BASE64 --repo nightBaker/fleans
   gh secret set NUGET_SIGNING_CERT_PASSWORD --repo nightBaker/fleans --body '<strong-password>'
   ```

   These must be set on the `nuget-publish` environment (the publish job runs under
   `environment: nuget-publish` with manual approval).

4. **Delete the local private material** once the secrets are set:

   ```bash
   rm -f tools/signing/fleans-signing.key tools/signing/fleans-signing.crt tools/signing/fleans-signing.pfx
   ```

5. **Dry-run to validate the signing chain end-to-end** (see note below on the secret
   dependency):

   ```bash
   gh workflow run nuget-publish.yml -f version=0.0.0-ci-test
   ```

   The dry-run packs + signs + verifies + uploads the artifacts, but skips the actual
   `dotnet nuget push`. Download the artifact bundle and confirm `dotnet nuget verify`
   passes against the new fingerprint.

6. **Update the published fingerprint** in the consumer docs
   (`website/src/content/docs/guides/writing-custom-tasks.mdx`, "Verifying package
   signatures") and in the next GitHub release notes.

## Dry-run secret dependency

The `0.0.0-ci-test` dry-run only exercises signing if `NUGET_SIGNING_CERT_PFX_BASE64` is
present on the `nuget-publish` environment. If it is unset, the Sign/Verify steps skip
(`HAS_SIGNING_CERT != 'true'`) and a green dry-run proves nothing about signing. After
step 3, the secret is present, so the dry-run is a true end-to-end check. For a
secret-independent local check, sign a locally-packed `.nupkg` with `generate-cert.sh`'s
output and run `dotnet nuget verify`.

## Durability of already-published packages

Signatures are RFC 3161 timestamped (`--timestamper http://timestamp.digicert.com`), so a
package signed while the cert was valid **remains verifiable after the cert expires** — the
timestamp authority's chain proves the signature predates expiry. Consumers do not need to
re-pull packages on rotation; old signatures stay valid for the lifetime of the timestamp
authority's chain (typically 10+ years).

## Future: CA-issued / SignPath

The self-signed cert is a pre-1.0 choice (free, ships fast). To give consumers a
warning-free `dotnet nuget verify --all` out of the box, migrate to a publicly-trusted
certificate — [SignPath.io](https://signpath.io/) offers a free CA-issued cert to OSS
projects (CLA + ~1–2 week onboarding). The signing infrastructure here accepts a different
cert with no workflow code change: regenerate/obtain the cert, commit the new public
`.cer`, update the two secrets, and drop the consumer-side trust step from the docs.
