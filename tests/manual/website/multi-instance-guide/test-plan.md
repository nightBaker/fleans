# Multi-Instance Activities guide — website regression

## Scenario

The website docs include a **guides/multi-instance-activities/** page that
explains BPMN multi-instance loops (parallel-cardinality, parallel-collection,
sequential-collection), anchored to the runnable manual-test fixtures under
`tests/manual/13-multi-instance/`. The page renders under the **Getting
Started** sidebar group between *Variables and Scope* and *Writing Custom-Task
Plugins*.

This plan verifies that the build completes cleanly, the page renders in both
themes, every cited manual-test fixture is referenced by name, the Limitations
admonition explicitly lists `completionCondition` and `nrOf*` as unsupported
with a link to the follow-up issue, the cross-link to *Variables and Scope*
resolves, and the drift-guard line ranges still match the current source SHA.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on ports 4321/4327/4328.

## Steps

1. **Build check.**
   ```bash
   cd website
   npm run build
   ```
   The build must complete with exit code 0. No Starlight content-collection
   errors. The output `dist/guides/multi-instance-activities/index.html` must
   exist. The total guide page count should be the previous count + 1.

2. **Dev server render — light theme.**
   ```bash
   cd website
   npm run dev
   ```
   Open `http://localhost:4321/fleans/guides/multi-instance-activities/` in a
   desktop browser. Toggle to light theme.
   - Page heading reads **Multi-Instance Activities**.
   - Sidebar (under *Getting Started*) shows **Multi-Instance Activities**
     sandwiched between *Variables and Scope* and *Writing Custom-Task Plugins*.
   - The *When to use multi-instance* table renders three rows.
   - The *What multi-instance can wrap* section lists six wrappable elements
     plus the transaction exclusion.
   - The `:::caution` Limitations admonition renders as a yellow callout (not
     raw `:::` text).

3. **Dev server render — dark theme.** Toggle to dark.
   - Page heading remains visible.
   - The Limitations caution admonition retains its warning style (no
     white-on-white, no missing icon glyph).
   - Inline `<bpmn:…>` and `<multiInstanceLoopCharacteristics …>` code blocks
     render with the standard syntax highlighting used by `variables-and-scope.md`
     and `error-handling.md`.

4. **Content spot-checks — fixture references.** Use the page's in-browser
   "Find on page" (`Ctrl+F`) to confirm each of these strings appears at least
   once in the rendered guide:
   - `tests/manual/13-multi-instance/parallel-cardinality.bpmn`
   - `tests/manual/13-multi-instance/parallel-collection.bpmn`
   - `tests/manual/13-multi-instance/sequential-collection.bpmn`

5. **Content spot-check — Limitations admonition wording.** The Limitations
   `:::caution` block must explicitly mention BOTH:
   - `completionCondition` is not honoured.
   - `nrOfInstances` / `nrOfActiveInstances` / `nrOfCompletedInstances` are
     not bound (only `loopCounter` is exposed).

   Both bullets must link to the follow-up GitHub issue (currently #470).

6. **Cross-link smoke — Variables and Scope.** From the *Loop-variable scope*
   section, click the inline link to `/fleans/guides/variables-and-scope/`.
   The link must return 200 and land on the *Variables and Scope* guide.

7. **Drift-guard freshness.** Open
   `website/src/content/docs/guides/multi-instance-activities.md` in the repo.
   The HTML drift-guard comment at the top pins line ranges against a verified
   branch SHA. For each pin, run:
   ```bash
   # MultiInstanceActivity record + ctor
   sed -n '1,5p' src/Fleans/Fleans.Domain/Activities/MultiInstanceActivity.cs
   wc -l   src/Fleans/Fleans.Domain/Activities/MultiInstanceActivity.cs

   # MultiInstanceCoordinator key bindings
   awk 'NR==34 || NR==76 || NR==96 || NR==118 || NR==135 || NR==159 {print NR":"$0}' \
     src/Fleans/Fleans.Domain/Aggregates/Services/MultiInstanceCoordinator.cs

   # ProcessSpawnActivity multi-instance branch
   sed -n '842,907p' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs | head -5
   awk 'NR==855 {print NR":"$0}' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs

   # BpmnConverter call sites
   awk 'NR==307 || NR==325 || NR==348 || NR==363 || NR==563 || NR==632 || NR==1130 {print NR":"$0}' \
     src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs

   # Transaction reject
   sed -n '578,581p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
   ```
   Each `awk`/`sed` must reference the symbol named in the pin (e.g. line 118
   must contain `iterDict["loopCounter"] = nextIndex`, line 855 must contain
   `iterDict["loopCounter"] = spawn.MultiInstanceIndex`, line 307 must contain
   `TryWrapMultiInstance(task`, line 578-581 must contain the transaction
   reject `InvalidOperationException`).

   If any pin is stale, **regenerate the drift-guard block at the current SHA
   and update the citations in the guide accordingly** — do not leave drift in
   place.

8. **Production build sanity** (final pass after step 1, repeated once you've
   made any drift fixes from step 7).
   - `Ctrl+C` the dev server.
   - `npm run build` from `website/` exits 0.

## Expected outcomes (checklist)

- [ ] `npm run build` exits 0; `dist/guides/multi-instance-activities/index.html` exists.
- [ ] Light-theme render: page + sidebar entry + `:::caution` admonition
      render correctly.
- [ ] Dark-theme render: same, with no contrast regressions.
- [ ] All three cited fixture paths
      (`tests/manual/13-multi-instance/parallel-cardinality.bpmn`,
      `parallel-collection.bpmn`, `sequential-collection.bpmn`)
      are referenced by name in the rendered guide.
- [ ] *Limitations* callout explicitly lists `completionCondition` and
      `nrOf*` (`nrOfInstances` / `nrOfActiveInstances` /
      `nrOfCompletedInstances`) as unsupported and links to issue #470.
- [ ] In-page link to `/fleans/guides/variables-and-scope/` returns 200.
- [ ] Every drift-guard pin (`MultiInstanceActivity.cs:1-126`,
      `MultiInstanceCoordinator.cs:34,76,96,118,135,159`,
      `WorkflowExecution.cs:842-907,855`,
      `BpmnConverter.cs:307,325,348,363,563,578-581,632,1130-1158`)
      still resolves to the named symbols at the current branch SHA.
