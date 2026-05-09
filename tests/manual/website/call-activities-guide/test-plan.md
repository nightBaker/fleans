# Call Activities and Sub-Processes guide — website regression

## Scenario

The website docs include a **guides/call-activities-and-subprocesses/** page
that consolidates the three composition primitives Fleans implements
(Embedded SubProcess, Call Activity, Transaction Sub-Process), anchored to
the runnable manual-test fixtures under `tests/manual/06-call-activity/`,
`tests/manual/07-subprocess/`, `tests/manual/11-error-boundary/`, and
`tests/manual/26-transaction-subprocess/`. The page renders under the
**Getting Started** sidebar group between *Variables and Scope* and
*Writing Custom-Task Plugins*.

This plan verifies that the build completes cleanly, the page renders in
both themes, every cited manual-test fixture is referenced by name, the
explicit `<zeebe:input>` / `<zeebe:output>` disclaimer is present, the
KNOWN BUG #11 callout mirrors `tests/manual/11-error-boundary/test-plan.md`,
and the drift-guard line ranges still match the current source SHA.

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
   warnings about `guides/call-activities-and-subprocesses`. The output
   `dist/guides/call-activities-and-subprocesses/index.html` must exist.

2. **Dev server render — light theme.**
   ```bash
   cd website
   npm run dev
   ```
   Open `http://localhost:4321/fleans/guides/call-activities-and-subprocesses/`
   in a desktop browser. Toggle to light theme.
   - Page heading reads **Call Activities and Sub-Processes**.
   - Sidebar (under *Getting Started*) shows **Call Activities and
     Sub-Processes** sandwiched between *Variables and Scope* and
     *Writing Custom-Task Plugins*.
   - The "When to use what" table renders three rows (Embedded SubProcess /
     Call Activity / Transaction Sub-Process) and four columns.
   - Both `:::caution` admonitions render as orange/yellow callouts —
     *`<zeebe:input>` / `<zeebe:output>` are NOT accepted by CallActivity*
     and *Known limitation: child errors don't bubble to parent CallActivity
     boundary*.

3. **Dev server render — dark theme.** Toggle to dark.
   - Page heading remains visible.
   - Both caution admonitions retain their warning style (no
     white-on-white, no missing icon glyph).
   - Inline `<bpmn:…>` code blocks render with the standard syntax
     highlighting used by `service-tasks.md` and `error-handling.md`.

4. **Content spot-checks — fixture references.** Use the page's in-browser
   "Find on page" (`Ctrl+F`) to confirm each of these strings appears at
   least once in the rendered guide:
   - `tests/manual/06-call-activity/`
   - `tests/manual/07-subprocess/`
   - `tests/manual/11-error-boundary/`
   - `tests/manual/26-transaction-subprocess/`

5. **Content spot-check — explicit zeebe disclaimer.** The guide MUST
   contain a callout that states `<zeebe:input>` / `<zeebe:output>` are
   **not** accepted by CallActivity, and that mixing the two forms
   silently produces zero mappings. Search the rendered page for:
   - `<zeebe:input>` (the literal string in the warning heading)
   - `silently produce` **zero** (the failure-mode wording)
   - `BpmnConverter.cs:1286-1322` (the parser-path disambiguation pin)

6. **Content spot-check — cross-links.** Confirm the following anchor
   links resolve (HTTP 200 + page renders, no Starlight 404):
   - `/fleans/guides/variables-and-scope/` — referenced from the *When to
     use what* table and the *Embedded SubProcess* section.
   - `/fleans/guides/error-handling/` — referenced from the *Error
     propagation* section, the KNOWN BUG callout, and the *Transaction
     sub-process* section.
   - `/fleans/concepts/bpmn-support/` — referenced from the *See also*
     section.

7. **Content spot-check — fixture-derived snippet.** The CallActivity XML
   block under *Variable mapping syntax — bare `<inputMapping>` /
   `<outputMapping>` only* must contain:
   ```xml
   <inputMapping source="input" target="input" />
   <outputMapping source="result" target="result" />
   ```
   (verbatim from `tests/manual/06-call-activity/parent-process.bpmn`).

8. **Content spot-check — KNOWN BUG wording mirrors fixture #11.** The
   guide's *Known limitation: child errors don't bubble…* callout describes
   the same condition as the `> **KNOWN BUG:**` block at the top of
   `tests/manual/11-error-boundary/test-plan.md`:
   > Child process errors don't propagate to parent error boundary on
   > CallActivity. The CallActivity stays Running indefinitely.
   Both source-of-truth wordings must agree on (a) what stays Running and
   (b) the workaround (catch inside the child, exit cleanly, branch on a
   variable in the parent).

9. **Drift-guard freshness.** Open
   `website/src/content/docs/guides/call-activities-and-subprocesses.md`
   in the repo. The HTML drift-guard comment at the top pins line ranges
   against a verified branch SHA. For each pin, run:
   ```bash
   # Call activity envelope
   sed -n '601,635p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | head -3
   sed -n '601,635p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | tail -3

   # Mapping match (LocalName == "inputMapping" / "outputMapping")
   sed -n '616p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
   sed -n '623p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs

   # Zeebe parser (NOT call-activity)
   sed -n '1286,1322p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | head -3

   # GetLatestDefinition single call site
   sed -n '61p' src/Fleans/Fleans.Application/Effects/WorkflowLifecycleEffectHandler.cs

   # CallActivity record fields
   sed -n '8,14p' src/Fleans/Fleans.Domain/Activities/CallActivity.cs
   ```
   Each excerpt must reference the symbol named in the pin:
   - `BpmnConverter.cs:601-635` — opens with
     `foreach (var callActivityEl in scopeElement.Elements(Bpmn + "callActivity"))`
   - `BpmnConverter.cs:616` — contains
     `e.Name.LocalName == "inputMapping"`
   - `BpmnConverter.cs:623` — contains
     `e.Name.LocalName == "outputMapping"`
   - `BpmnConverter.cs:1286-1322` — references `<zeebe:input>` and
     `<zeebe:output>` parsing, ParseInputMapping / ParseOutputMapping
   - `WorkflowLifecycleEffectHandler.cs:61` — contains
     `processGrain.GetLatestDefinition()`
   - `CallActivity.cs:8-14` — declares the record with
     `PropagateAllParentVariables = true` and
     `PropagateAllChildVariables = true`

   If any pin is stale, **regenerate the drift-guard block at the current
   SHA and update the citations in the guide accordingly** — do not leave
   drift in place.

10. **Production build sanity** (final pass after step 1, repeated once
    you've made any drift fixes from step 9).
    - `Ctrl+C` the dev server.
    - `npm run build` from `website/` exits 0.

## Expected outcomes (checklist)

- [ ] `npm run build` exits 0;
      `dist/guides/call-activities-and-subprocesses/index.html` exists.
- [ ] Light-theme render: page + sidebar entry between *Variables and
      Scope* and *Writing Custom-Task Plugins* + both `:::caution`
      admonitions render correctly.
- [ ] Dark-theme render: same, with no contrast regressions.
- [ ] All four fixture-folder paths (#06, #07, #11, #26) are referenced by
      name in the rendered guide.
- [ ] Explicit `<zeebe:input>` / `<zeebe:output>` disclaimer present, with
      the *silently produce zero mappings* failure-mode wording and the
      `BpmnConverter.cs:1286-1322` disambiguation pin.
- [ ] Cross-links to *Variables and Scope*, *Error Handling*, and *BPMN
      Support* resolve (HTTP 200).
- [ ] Fixture-derived `<callActivity>` snippet appears verbatim from
      `tests/manual/06-call-activity/parent-process.bpmn`.
- [ ] *Known limitation* callout wording matches the KNOWN BUG note at the
      top of `tests/manual/11-error-boundary/test-plan.md`.
- [ ] Every drift-guard pin
      (`BpmnConverter.cs:601-635,616-625,1286-1322`,
      `WorkflowLifecycleEffectHandler.cs:61`,
      `CallActivity.cs:8-14`) still resolves to the named symbol at the
      current branch SHA.
