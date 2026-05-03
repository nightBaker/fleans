# Error Handling guide — website regression

## Scenario

The website docs include a **guides/error-handling/** page that consolidates the
three BPMN failure-recovery mechanisms Fleans implements (errors, escalations,
compensation), anchored to the runnable manual-test fixtures. The page renders
under the **Getting Started** sidebar group between *Hosting Plugins (Custom
Worker Host)* and *BPMN Editor*.

This plan verifies that the build completes cleanly, the page renders in both
themes, every cited manual-test fixture is referenced by name, the KNOWN BUG
disclosure mirrors `tests/manual/11-error-boundary/test-plan.md`, and the
drift-guard line ranges still match the current source SHA.

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
   warnings about `guides/error-handling`. The output `dist/guides/error-handling/index.html`
   must exist.

2. **Dev server render — light theme.**
   ```bash
   cd website
   npm run dev
   ```
   Open `http://localhost:4321/fleans/guides/error-handling/` in a desktop
   browser. Toggle to light theme.
   - Page heading reads **Error Handling**.
   - Sidebar (under *Getting Started*) shows **Error Handling** sandwiched
     between *Hosting Plugins (Custom Worker Host)* and *BPMN Editor*.
   - The "When to use what" table renders three rows (Error / Escalation /
     Compensation) and four columns.
   - Both `:::caution` admonitions render as orange/yellow callouts —
     *Known limitation: child-process errors don't bubble to parent CallActivity*
     and *Variable-scope invariant — read this before writing handlers*.

3. **Dev server render — dark theme.** Toggle to dark.
   - Page heading remains visible.
   - The two caution admonitions retain their warning style (no white-on-white,
     no missing icon glyph).
   - Inline `<bpmn:…>` code blocks render with the standard syntax highlighting
     used by `service-tasks.md` and `user-tasks.md`.

4. **Content spot-checks — fixture references.** Use the page's in-browser
   "Find on page" (`Ctrl+F`) to confirm each of these strings appears at least
   once in the rendered guide:
   - `tests/manual/11-error-boundary/`
   - `tests/manual/19-event-subprocess-error/`
   - `tests/manual/24-escalation-event/`
   - `tests/manual/24-compensation-event/`

5. **Content spot-check — KNOWN BUG wording mirrors fixture #11.** The guide's
   *Known limitation* callout describes the same condition as the
   `> **KNOWN BUG:**` block at the top of
   `tests/manual/11-error-boundary/test-plan.md`:
   > Child process errors don't propagate to parent error boundary on
   > CallActivity. The CallActivity stays Running indefinitely.
   Both source-of-truth wordings must agree on (a) what stays Running and
   (b) the workaround.

6. **Drift-guard freshness.** Open `website/src/content/docs/guides/error-handling.md`
   in the repo. The HTML drift-guard comment at the top pins line ranges
   against a verified branch SHA. For each pin, run:
   ```bash
   # AdvanceCompensationWalkIfHandlerCompleted
   sed -n '723,784p' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs | head -5
   sed -n '723,784p' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs | tail -3

   # BpmnConverter error parsing
   sed -n '132p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
   sed -n '209,269p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | head -5
   sed -n '665,710p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | head -5

   # Compensation validation
   sed -n '759,815p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs | head -5

   # Error exceptions
   sed -n '5,13p' src/Fleans/Fleans.Domain/Errors/BadRequestActivityException.cs
   sed -n '5,22p' src/Fleans/Fleans.Domain/Errors/CustomTaskFailedActivityException.cs

   # _context binding
   sed -n '46p' src/Fleans/Fleans.Infrastructure/Scripts/DynamicExpressoScriptExpressionExecutor.cs
   ```
   Each excerpt must reference the symbol named in the pin (e.g. line 132 must
   contain `errorEventDefinition`, line 723 must contain
   `AdvanceCompensationWalkIfHandlerCompleted`, line 767 must contain
   `Emit(new VariablesMerged(parentVariablesId, handlerVariables))`).

   If any pin is stale, **regenerate the drift-guard block at the current SHA
   and update the citations in the guide accordingly** — do not leave drift in
   place.

7. **Production build sanity** (final pass after step 1, repeated once you've
   made any drift fixes from step 6).
   - `Ctrl+C` the dev server.
   - `npm run build` from `website/` exits 0.

## Expected outcomes (checklist)

- [ ] `npm run build` exits 0; `dist/guides/error-handling/index.html` exists.
- [ ] Light-theme render: page + sidebar entry + both `:::caution` admonitions
      render correctly.
- [ ] Dark-theme render: same, with no contrast regressions.
- [ ] All four fixture-folder paths (#11, #19, #24-escalation, #24-compensation)
      are referenced by name in the rendered guide.
- [ ] *Known limitation* callout wording matches the KNOWN BUG note at the top
      of `tests/manual/11-error-boundary/test-plan.md`.
- [ ] Every drift-guard pin (`WorkflowExecution.cs:723-784`,
      `BpmnConverter.cs:132,209-269,665-710,759-815`,
      `BadRequestActivityException.cs:5-13`,
      `CustomTaskFailedActivityException.cs:5-22`,
      `DynamicExpressoScriptExpressionExecutor.cs:46`) still resolves to the
      named symbol at the current branch SHA.
