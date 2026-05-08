# AddDockerComposeEnvironment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `aspire publish -t docker-compose` actually emit a Compose Spec YAML so the release pipeline's `docker-compose-v<VERSION>.zip` artifact is usable, and add a fail-loud assertion so this regression cannot recur silently.

**Architecture:** Register a Docker Compose environment alongside the existing Kubernetes environment in the Aspire apphost; assert the produced compose file exists before zipping in CI; update the release-pipeline test plan with a new Pitfall and tightened Scenario 2 expectation.

**Tech Stack:** .NET 10 / Aspire 13.2.3 (`Aspire.Hosting.Docker` package, already referenced in the apphost csproj) / GitHub Actions / bash.

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/Fleans/Fleans.Aspire/Program.cs` | Modify | Register Compose environment alongside the existing K8s environment so `aspire publish -t docker-compose` has a real target. |
| `.github/workflows/release.yml` | Modify | Add a pre-zip assertion in the `compose` job that fails loudly if `aspire publish -t docker-compose` produced no compose YAML. |
| `tests/manual/42-release-pipeline/test-plan.md` | Modify | Document the regression class (Pitfall #8) and tighten Scenario 2 expectation. |

---

## Task 1: Register `AddDockerComposeEnvironment` in the apphost

**Files:**
- Modify: `src/Fleans/Fleans.Aspire/Program.cs:6` (insert one line after the existing `AddKubernetesEnvironment` call)

The apphost currently registers only a Kubernetes environment:

```csharp
// Kubernetes publish target — `aspire publish -t kubernetes -o out/k8s` emits manifests for
// every Aspire-hosted service plus its dependencies (Redis, optional PostgreSQL/Kafka). The
// "k8s" name is the resource id in the AppHost model; the publish target type is "kubernetes".
builder.AddKubernetesEnvironment("k8s");
```

Without an `AddDockerComposeEnvironment(...)` call, `aspire publish -t docker-compose` silently routes to the K8s publisher and emits a Helm chart into the output dir — exactly the bug we are fixing.

- [ ] **Step 1: Add the Compose environment registration**

Edit `src/Fleans/Fleans.Aspire/Program.cs` to add a new block immediately after the existing `AddKubernetesEnvironment("k8s");` (line 6). The new content:

```csharp
// Docker Compose publish target — `aspire publish -t docker-compose -o out/compose` emits a
// Compose Spec YAML referencing every Aspire-hosted service plus its dependencies. Required
// for the release pipeline's `compose` job; without this call, `aspire publish -t docker-compose`
// silently routes to whichever publisher IS registered (Kubernetes here) and produces unusable
// output. See tests/manual/42-release-pipeline/test-plan.md Pitfall #8.
builder.AddDockerComposeEnvironment("compose");
```

The package providing `AddDockerComposeEnvironment` (`Aspire.Hosting.Docker` 13.2.3) is already a `<PackageReference>` in `src/Fleans/Fleans.Aspire/Fleans.Aspire.csproj` (line 17), so no csproj edit is needed.

- [ ] **Step 2: Confirm the apphost still builds (no namespace surprises)**

Run from `src/Fleans/`:

```bash
cd src/Fleans
dotnet build Fleans.Aspire/Fleans.Aspire.csproj -c Release --nologo -v q
```

Expected: `0 Error(s)`. Pre-existing warnings (CS8619, CS8604 etc.) are fine; no NEW errors should appear.

If the compiler reports `'IDistributedApplicationBuilder' does not contain a definition for 'AddDockerComposeEnvironment'`, the using directive is missing. The extension method lives in the `Aspire.Hosting` namespace (auto-imported via `<ImplicitUsings>enable</ImplicitUsings>` in the csproj) — if implicit usings are not picking it up, add `using Aspire.Hosting;` at the top of `Program.cs`.

- [ ] **Step 3: Confirm the apphost still starts in dev mode (no breaking publish-only registration)**

Run from `src/Fleans/`:

```bash
dotnet run --project Fleans.Aspire --no-build -- --dotnet-cli-host-no-launch-profile 2>&1 | head -25
# or equivalently:
dotnet run --project Fleans.Aspire 2>&1 | head -25
```

Wait ~10 seconds, then Ctrl+C. Expected: the Aspire dashboard URL prints, no `InvalidOperationException` referencing publishers. If the apphost crashes during dev startup, revert the change and stop — `AddDockerComposeEnvironment` should be a no-op in dev mode, but if 13.2.3-preview behaves differently we need to gate it on `builder.ExecutionContext.IsPublishMode`.

- [ ] **Step 4: Verify `aspire publish -t docker-compose` emits a compose YAML locally**

The Aspire CLI must be installed once per machine:

```bash
dotnet tool install -g Aspire.Cli --prerelease 2>&1 | tail -3
# If already installed: dotnet tool update -g Aspire.Cli --prerelease
export PATH="$HOME/.dotnet/tools:$PATH"
aspire --version
```

Then publish:

```bash
cd src/Fleans
rm -rf /tmp/aspire-compose
aspire publish --project Fleans.Aspire -t docker-compose -o /tmp/aspire-compose
ls -la /tmp/aspire-compose/
```

Expected: `/tmp/aspire-compose/` contains a `compose.yaml` (or `docker-compose.yaml`). NOT `Chart.yaml` or `templates/`.

- [ ] **Step 5: Confirm the produced compose file is valid Compose Spec**

```bash
docker compose -f /tmp/aspire-compose/compose.yaml config | head -50
```

Expected: the rendered config prints services for at least `fleans-core`, `fleans-management`, `fleans-mcp`, `fleans-worker`, `fleans-custom-worker`, plus Redis. Image refs may be `fleans-core:latest` (no registry) at this stage — that's a known Aspire 13.x behavior, addressed via the `WithContainerRegistry(...)` preview API in a later iteration; out of scope for this PR. The point of this step is "is it parseable Compose Spec" — yes if `docker compose ... config` exits 0.

- [ ] **Step 6: Commit**

```bash
cd /Users/erasyl.shalabaev/Desktop/projects/fleans
git add src/Fleans/Fleans.Aspire/Program.cs
git commit -m "$(cat <<'EOF'
fix(aspire): register AddDockerComposeEnvironment so -t docker-compose works

The release pipeline's `compose` job runs `aspire publish -t docker-compose
-o out/compose` and zips the result as docker-compose-v<VERSION>.zip. The
zip currently contains an Aspire-auto-generated Helm chart (Chart.yaml +
templates/<svc>/{deployment,service,config,secrets}.yaml) instead of a
compose YAML, because Program.cs registers only AddKubernetesEnvironment
and never a Compose environment. Aspire silently routes -t docker-compose
to the K8s publisher in that case.

Register AddDockerComposeEnvironment("compose") alongside the existing
AddKubernetesEnvironment("k8s") so aspire publish -t docker-compose has a
real target. Both publishers stay registered; dev mode (`dotnet run
--project Fleans.Aspire`) is unchanged. The FLEANS_LOAD_TEST_MODE bind-
mount gate stays as-is — the K8s publisher's BeforeStart hook still fires
on every aspire publish regardless of -t target.

Spec: docs/superpowers/specs/2026-05-08-aspire-docker-compose-environment-design.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Add a fail-loud pre-zip assertion in `release.yml`

**Files:**
- Modify: `.github/workflows/release.yml` (the `compose` job's "Zip the compose bundle" step, around line 198-203)

Currently the step is:

```yaml
- name: Zip the compose bundle
  run: |
    set -euo pipefail
    ZIP="docker-compose-v${{ needs.setup.outputs.version }}.zip"
    (cd out/compose && zip -r "../../$ZIP" .)
    ls -la "$ZIP"
```

It will happily zip whatever is in `out/compose/`, including the broken helm-shaped output that landed there before Task 1. We need a guard that fails loudly if the produced contents do not include a compose YAML, so a future regression of Task 1 (e.g., someone accidentally removing the `AddDockerComposeEnvironment` line) cannot ship a wrong-shape artifact.

- [ ] **Step 1: Edit the step to insert the assertion before the zip**

Replace the step body so it asserts the file exists first:

```yaml
- name: Zip the compose bundle
  run: |
    set -euo pipefail
    # Aspire 13.x emits compose.yaml; fall back to docker-compose.yaml in case a
    # future Aspire version renames it. If neither exists, AddDockerComposeEnvironment
    # is missing in Program.cs (or the publisher silently routed to a different env)
    # — fail loudly rather than ship a wrong-shape artifact.
    if [ ! -f out/compose/compose.yaml ] && [ ! -f out/compose/docker-compose.yaml ]; then
      echo "::error::aspire publish -t docker-compose produced no compose file in out/compose/"
      echo "  out/compose/ contents:"
      ls -la out/compose/
      exit 1
    fi
    ZIP="docker-compose-v${{ needs.setup.outputs.version }}.zip"
    (cd out/compose && zip -r "../../$ZIP" .)
    ls -la "$ZIP"
```

- [ ] **Step 2: Lint the workflow YAML**

```bash
cd /Users/erasyl.shalabaev/Desktop/projects/fleans
grep -n "Zip the compose bundle" .github/workflows/release.yml
# Should find exactly one match.

# Quick structural check — count jobs to confirm YAML still parses at the top level.
grep -nE "^  [a-z]+(-[a-z]+)?:|^jobs:" .github/workflows/release.yml | head -10
# Expect: jobs: + setup + images + compose + helm-package + release
```

If you have `yamllint` or `actionlint` installed:

```bash
yamllint .github/workflows/release.yml || true
actionlint .github/workflows/release.yml || true
```

Don't fail Task 2 on yamllint/actionlint output noise — those tools aren't installed by default; the structural grep is the load-bearing check.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "$(cat <<'EOF'
ci(release): assert compose YAML exists before zipping

If a future change accidentally drops AddDockerComposeEnvironment from the
apphost (or Aspire renames the published file), the `compose` job would
silently zip whatever helm-shaped output landed in out/compose/. Add a
pre-zip assertion that requires either compose.yaml or docker-compose.yaml
at the root of out/compose/ before the zip step runs, with a `::error::`
annotation that surfaces the directory contents on failure.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Update test plan #42 (Pitfall #8 + Scenario 2 expectation)

**Files:**
- Modify: `tests/manual/42-release-pipeline/test-plan.md` (Scenario 2 expected outcomes block + Pitfalls section)

- [ ] **Step 1: Tighten Scenario 2 expected outcome (b)**

Find the expected-outcomes block under "### 2. Dispatch dry-run (no real release)" and locate the bullet `(b)`. Currently it reads:

```markdown
- (b) The `compose` job succeeds and `docker-compose-v0.0.0-rc-test.zip` is uploaded as a workflow artifact.
```

Replace with:

```markdown
- (b) The `compose` job succeeds, `out/compose/compose.yaml` (or `docker-compose.yaml`) exists pre-zip (the assertion at the top of the "Zip the compose bundle" step did not fire), and `docker-compose-v0.0.0-rc-test.zip` is uploaded as a workflow artifact whose root contains `compose.yaml`.
```

- [ ] **Step 2: Append Pitfall #8 to the Pitfalls section**

Find the line in the Pitfalls section that introduces the count (currently `Seven issues hit early dispatches and were fixed before any tag shipped — keep them in mind when editing release.yml or Fleans.Aspire/Program.cs:`) and bump the count from `Seven` to `Eight`.

After the existing Pitfall #7 (which ends with "Keep the helm path as `helm lint` + `helm template` smoke render; do not re-introduce the deep-diff."), append a new entry:

```markdown
8. **Both `AddKubernetesEnvironment(...)` and `AddDockerComposeEnvironment(...)` must be registered in the apphost when `release.yml` invokes both `-t kubernetes` and `-t docker-compose`.** Without the matching `AddXxxEnvironment` call, Aspire silently routes the publish to whichever environment IS registered. The `compose` job ran for months emitting an Aspire-auto-generated Helm chart (`Chart.yaml` + `templates/<svc>/...`) into `out/compose/` because only `AddKubernetesEnvironment("k8s")` was registered; the zip step happily packed the wrong shape. Fixed by adding `builder.AddDockerComposeEnvironment("compose")` to `Program.cs` and a pre-zip assertion in the `compose` job that fails on missing `compose.yaml`/`docker-compose.yaml`.
```

- [ ] **Step 3: Verify the rendered text**

```bash
grep -nE "^Eight issues|Pitfall|^[1-8]\." tests/manual/42-release-pipeline/test-plan.md | tail -12
```

Expect: count is `Eight`, items 1-8 are all present, and the new item 8 mentions both `AddKubernetesEnvironment` and `AddDockerComposeEnvironment`.

- [ ] **Step 4: Commit**

```bash
git add tests/manual/42-release-pipeline/test-plan.md
git commit -m "$(cat <<'EOF'
docs(test-plan): document AddDockerComposeEnvironment pitfall (#42 Pitfall 8)

Tighten Scenario 2 expected outcome (b) to require a real compose YAML
inside the produced docker-compose-v<VERSION>.zip, not just artifact
upload success. Add Pitfall #8 explaining the silent-route-to-K8s
behavior that produced months of broken docker-compose bundles.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Push branch, open PR, auto-merge, dispatch dry-run, verify

**Files:** none (orchestration only).

The user's preferred workflow from PRs #501-#504 is small focused PRs that auto-merge once CI is green. The branch already exists (`spec/aspire-docker-compose-environment`) from the brainstorming step; the three commits from Tasks 1-3 sit on top of it.

- [ ] **Step 1: Push the branch**

```bash
cd /Users/erasyl.shalabaev/Desktop/projects/fleans
git push -u origin spec/aspire-docker-compose-environment 2>&1 | tail -3
```

- [ ] **Step 2: Open the PR**

```bash
gh pr create --title "fix(release): wire up AddDockerComposeEnvironment + assert compose YAML pre-zip" --body "$(cat <<'EOF'
## Summary

Closes the broken `compose` release-job artifact. The zip currently contains an Aspire-auto-generated Helm chart (`Chart.yaml` + `templates/<svc>/...`) because the apphost registered only `AddKubernetesEnvironment("k8s")` and never a Compose environment, so `aspire publish -t docker-compose` silently routed to the K8s publisher. The hand-written `charts/fleans/` chart packaged by the `helm-package` job remains the supported install path — Aspire 13.x's helm output cannot match the hand-written chart's operator-tunable knobs (replicas/resources/scheduling/ingress/NOTES/helpers), so we don't ship the auto-emit version.

- **`Fleans.Aspire/Program.cs`** — register `builder.AddDockerComposeEnvironment("compose")` alongside the existing `AddKubernetesEnvironment("k8s")`. Both publishers stay registered. Dev mode unchanged. `FLEANS_LOAD_TEST_MODE` bind-mount gate stays as-is.
- **`.github/workflows/release.yml`** — pre-zip assertion in the `compose` job: fail loudly with `::error::` annotation if neither `compose.yaml` nor `docker-compose.yaml` lands in `out/compose/`.
- **`tests/manual/42-release-pipeline/test-plan.md`** — Scenario 2 expectation (b) tightened; Pitfall #8 documents the silent-route-to-K8s behavior so the next maintainer doesn't re-discover it.

Spec: `docs/superpowers/specs/2026-05-08-aspire-docker-compose-environment-design.md`.

## Test plan

- [x] Local: `aspire publish -t docker-compose -o /tmp/aspire-compose` produces `compose.yaml` and `docker compose -f /tmp/aspire-compose/compose.yaml config` parses cleanly.
- [x] Local: `dotnet build` exits 0; `dotnet run --project Fleans.Aspire` still launches the dev dashboard.
- [ ] Maintainer dispatches `gh workflow run release.yml --ref main -f version=0.0.0-rc-test` after merge and confirms:
  - `compose` job pre-zip assertion does not fire.
  - Downloaded `docker-compose-v0.0.0-rc-test.zip` contains `compose.yaml` at the root, NOT `Chart.yaml`/`templates/`.
  - All 5 services + Redis appear in the rendered config with `ghcr.io/nightbaker/fleans-<svc>` image refs.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)" 2>&1 | tail -3
```

- [ ] **Step 3: Enable auto-merge**

```bash
gh pr merge --squash --auto --delete-branch 2>&1 | tail -3
```

(`gh pr merge` without a number operates on the PR for the current branch.) The PR will auto-merge once `.github/workflows/build.yml` (the `.NET` workflow) and `.github/workflows/pg-tests.yml` (the `PostgreSQL tests` workflow) both pass.

- [ ] **Step 4: Wait for merge, switch to main, pull**

```bash
# Block on the PR finishing
gh pr view --json state,mergedAt --jq '"state=\(.state) merged=\(.mergedAt)"'
# Once mergedAt is non-empty:
git checkout main && git pull --ff-only
git log --oneline -3
```

The HEAD commit should be the squash-merge of this PR.

- [ ] **Step 5: Dispatch a dry-run on `main`**

```bash
gh workflow run release.yml --ref main -f version=0.0.0-rc-test
sleep 5
RUN_ID=$(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
echo "RUN_ID=$RUN_ID"
gh run watch "$RUN_ID" --exit-status
```

Expect: `success` exit. If any job fails, inspect with `gh run view "$RUN_ID" --log-failed` and stop the plan execution — the assertion may have surfaced a real Aspire 13.x quirk we need to address before re-attempting.

- [ ] **Step 6: Verify the produced artifact**

```bash
RUN_ID=$(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
mkdir -p /tmp/compose-artifact-verify
cd /tmp/compose-artifact-verify
gh run download "$RUN_ID" -n docker-compose-v0.0.0-rc-test
ls -la
unzip -l docker-compose-v0.0.0-rc-test.zip | head -20
echo "---"
unzip -p docker-compose-v0.0.0-rc-test.zip compose.yaml 2>/dev/null | head -30 \
  || unzip -p docker-compose-v0.0.0-rc-test.zip docker-compose.yaml 2>/dev/null | head -30
```

Expect: zip listing includes `compose.yaml` (or `docker-compose.yaml`), NO `Chart.yaml`, NO `templates/...` paths. The `services:` block at the top of the file references `fleans-core`, `fleans-management`, `fleans-mcp`, `fleans-worker`, `fleans-custom-worker`, plus `redis`/`orleans-redis`.

- [ ] **Step 7: Run the cleanup loop for this dispatch's ghcr.io versions**

The dispatch publishes 12 image versions per dry-run (4 services × {manifest list `0.0.0-rc-test`, per-RID `-x64`, `-arm64`}). The `gh` CLI needs `delete:packages` scope to delete them via API; if the user hasn't refreshed scopes, the manual-UI cleanup path from `tests/manual/website/cosign-signing/test-plan.md` is the fallback. Run the loop best-effort:

```bash
cd /Users/erasyl.shalabaev/Desktop/projects/fleans
for round in 1 2 3 4; do
  any=0
  for IMG in fleans-api fleans-web fleans-worker fleans-mcp; do
    for TAG in 0.0.0-rc-test 0.0.0-rc-test-x64 0.0.0-rc-test-arm64; do
      VID=$(gh api "/user/packages/container/$IMG/versions" --paginate \
        --jq '.[] | select(.metadata.container.tags | index("'"$TAG"'")) | .id' 2>/dev/null \
        | head -1)
      if [ -n "$VID" ]; then
        gh api -X DELETE "/user/packages/container/$IMG/versions/$VID" >/dev/null 2>&1 \
          && { echo "deleted $IMG #$VID (tag $TAG)"; any=1; }
      fi
    done
  done
  [ "$any" = "0" ] && { echo "no more matches"; break; }
done
```

If the loop reports `403 (read:packages scope)`, surface that to the user in the final summary so they can either refresh `gh auth refresh -h github.com -s read:packages,delete:packages` or clean up via the GitHub UI.

- [ ] **Step 8: Final summary**

Post a final summary listing:
- The merged PR number + title.
- The dispatch run ID + conclusion + per-job durations.
- The verified compose YAML's first 5 service names.
- Cleanup state (deleted N versions / scopes-missing fallback notice).

---

## Self-Review

**1. Spec coverage**

| Spec section | Task |
|---|---|
| Decision: register Compose env in apphost | Task 1 |
| Decision: pre-zip assertion in release.yml | Task 2 |
| Documentation updates → Pitfall #8 | Task 3 (Step 2) |
| Documentation updates → Scenario 2 expectation | Task 3 (Step 1) |
| Test plan → Local verification | Task 1 (Steps 4-5) |
| Test plan → CI verification | Task 4 (Steps 5-6) |
| Out of scope: K8s publisher customization | Not addressed (correctly) |
| Out of scope: load-test README change | Not addressed (correctly — works as-documented once Compose env is registered) |

All spec sections covered. No gaps.

**2. Placeholder scan**

- No "TBD" / "TODO" / "fill in details" anywhere.
- All file paths are exact (`src/Fleans/Fleans.Aspire/Program.cs:6`, `.github/workflows/release.yml`, `tests/manual/42-release-pipeline/test-plan.md`).
- All shell commands have explicit expected output / exit conditions.
- All code snippets are complete (no "..." in code blocks).

**3. Type/name consistency**

- `AddDockerComposeEnvironment("compose")` used consistently across Task 1, the spec reference, and the PR body.
- `compose.yaml` (with `docker-compose.yaml` fallback) used consistently across Tasks 2, 3, and 4.
- Job name `compose` (not `Aspire docker-compose bundle`, which is the `name:` display string) — the YAML key matches the assertion docs in Task 2 and the test-plan reference in Task 3.
- Pitfall numbering: existing test-plan has Pitfalls 1-7; Task 3 bumps the intro from `Seven` to `Eight` and appends Pitfall #8. Confirmed by grep in Task 3 Step 3.

No inconsistencies found.
