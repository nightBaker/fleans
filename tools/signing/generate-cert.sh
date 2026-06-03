#!/usr/bin/env bash
#
# generate-cert.sh — generate the self-signed code-signing certificate used to
# author-sign the Fleans NuGet plugin packages (issue #458).
#
# This is a MAINTAINER action, run once (and on rotation — see
# docs/runbooks/nuget-signing-rotation.md). It produces three artifacts:
#
#   fleans-signing.key   private key            — keep secret, never commit
#   fleans-signing.pfx   PKCS#12 bundle         — base64 -> GitHub secret, never commit
#   fleans-signing-cert.cer  public certificate (DER) — COMMIT this to tools/signing/
#
# The committed public .cer is the trust anchor consumers pin against; the CI
# workflow derives the expected SHA-256 fingerprint from it at verify time, so
# there is a single source of truth and nothing to keep in sync.
#
# Requirements: openssl (>= 1.1.1). Code-signing certs MUST carry the
# codeSigning EKU or `dotnet nuget sign` rejects them.
#
# Usage:
#   tools/signing/generate-cert.sh [PASSWORD]
# If PASSWORD is omitted you are prompted (the password also goes into the
# NUGET_SIGNING_CERT_PASSWORD GitHub secret).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUBJECT="/CN=Fleans Project self-signed code signing"
DAYS=1095   # 3 years — timestamped signatures stay valid past expiry regardless

PASSWORD="${1:-}"
if [ -z "$PASSWORD" ]; then
  read -r -s -p "PFX export password: " PASSWORD
  echo
fi
if [ -z "$PASSWORD" ]; then
  echo "ERROR: a non-empty PFX password is required." >&2
  exit 1
fi

KEY="${SCRIPT_DIR}/fleans-signing.key"
CRT="${SCRIPT_DIR}/fleans-signing.crt"
PFX="${SCRIPT_DIR}/fleans-signing.pfx"
CER="${SCRIPT_DIR}/fleans-signing-cert.cer"

echo "==> Generating self-signed code-signing certificate (${DAYS} days)"
openssl req -x509 -newkey rsa:4096 -sha256 -days "${DAYS}" -nodes \
  -keyout "${KEY}" -out "${CRT}" \
  -subj "${SUBJECT}" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=critical,codeSigning"

echo "==> Bundling private key + cert into PFX"
openssl pkcs12 -export -out "${PFX}" \
  -inkey "${KEY}" -in "${CRT}" \
  -passout "pass:${PASSWORD}"

echo "==> Exporting public certificate (DER) for the repo"
openssl x509 -in "${CRT}" -outform DER -out "${CER}"

FP="$(openssl x509 -in "${CRT}" -noout -fingerprint -sha256 | sed 's/.*=//; s/://g')"

cat <<EOF

==> Done.

  Public cert (COMMIT this):  ${CER}
  SHA-256 fingerprint:        ${FP}

Next maintainer steps (one-time setup / rotation):

  1. Commit the public cert:
       git add tools/signing/fleans-signing-cert.cer && git commit -m "..."

  2. Set the two repo secrets (private material — never committed):
       # Portable base64 (works on macOS and Linux):
       base64 < "${PFX}" | tr -d '\\n' \\
         | gh secret set NUGET_SIGNING_CERT_PFX_BASE64 --repo nightBaker/fleans
       gh secret set NUGET_SIGNING_CERT_PASSWORD --repo nightBaker/fleans --body '<password>'

  3. Delete the local private material once the secrets are set:
       rm -f "${KEY}" "${CRT}" "${PFX}"

The CI workflow derives the expected fingerprint from the committed .cer, so
there is nothing else to configure. Signing auto-skips when the secret is unset
(e.g. on forks).
EOF
