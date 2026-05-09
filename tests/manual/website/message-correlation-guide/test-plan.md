# Message Correlation guide — website regression

## Scenario

The website docs include a **guides/message-correlation/** page that documents
how Fleans correlates incoming BPMN messages to running workflow instances —
the BPMN definition (placement of `<extensionElements>` inside `<bpmn:message>`),
variable resolution semantics (`= ` prefix strip + plain `GetVariable` lookup,
no expression evaluation), the `POST /Workflow/message` API, an end-to-end curl
example, three patterns (request/response, event-driven start, multi-step
orchestration), and a set of common pitfalls. The page renders under the
**Getting Started** sidebar group between *Error Handling* and *BPMN Editor*.

This plan verifies that the build completes cleanly, the page renders in both
themes, every cited manual-test fixture is referenced by name, all `:::caution`
admonitions render correctly, the curl example renders as a copyable code block,
the cross-link to *Variables and Scope* resolves, and the drift-guard line
ranges still match the current source SHA.

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
   warnings about `guides/message-correlation`. The output
   `dist/guides/message-correlation/index.html` must exist.

2. **Dev server render — light theme.**
   ```bash
   cd website
   npm run dev
   ```
   Open `http://localhost:4321/fleans/guides/message-correlation/` in a desktop
   browser. Toggle to light theme.
   - Page heading reads **Message Correlation**.
   - Sidebar (under *Getting Started*) shows **Message Correlation** sandwiched
     between *Error Handling* and *BPMN Editor*.
   - The §"BPMN definition" XML snippet renders with syntax highlighting and
     contains the literal lines:
     - `xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"`
     - `<message id="msg1" name="approvalReceived">`
     - `<zeebe:subscription correlationKey="= requestId" />`
   - The §"Variable resolution semantics" C# snippet renders the
     `ResolveCorrelationKey` method body with the `[2..]` slice and the
     `InvalidOperationException` throw.

3. **Dev server render — dark theme.** Toggle to dark.
   - Page heading and sidebar entry remain visible.
   - All `:::caution` admonitions retain their warning style (no white-on-white,
     no missing icon glyph).
   - Code blocks (BPMN XML, C#, bash/curl) retain readable contrast.

4. **Content spot-checks — fixture references.** Use the page's in-browser
   "Find on page" (`Ctrl+F`) to confirm each of these strings appears at least
   once in the rendered guide:
   - `tests/manual/09-message-events/message-catch.bpmn`
   - `tests/manual/16-message-start-event/message-start-event.bpmn`
   - `tests/manual/21-event-subprocess-message/message-event-subprocess.bpmn`

5. **Content spot-checks — required admonitions render.** Confirm each of
   these `:::caution` blocks renders as an orange/yellow callout with title and
   body text:
   - "Place `<extensionElements>` inside `<bpmn:message>`, not inside the
     message-event element" — directly under the §"BPMN definition" snippet.
   - "Forgetting the `=` prefix" — under §"Common pitfalls".
   - "Compound expressions like `= a + b` are NOT supported" — under
     §"Common pitfalls". Includes a worked-around example with a `computeKey`
     script task.
   - "Variable not in scope at subscription time" — under §"Common pitfalls".
   - "`MessageName` is case-sensitive" — under §"Common pitfalls".
   - "Wrong `<extensionElements>` placement is silently ignored" — under
     §"Common pitfalls".
   - "Boundary message events on `IntermediateCatchEvent` do not register" —
     under §"Limitations" (this is the regression #9 KNOWN BUG disclosure).

6. **Content spot-check — curl example renders.** The §"End-to-end curl
   example" block must render as a single fenced `bash` code block containing
   three `curl` commands (deploy → start → message). Each must be copyable
   from the rendered page (Starlight's copy button must be available).

7. **Content spot-check — cross-links resolve.** Click each in the rendered
   guide; each must return HTTP 200:
   - The §"Variable resolution semantics" link to **Variables and Scope**
     (`/fleans/guides/variables-and-scope/`).
   - The §"Cookbook → Multi-step orchestration" link to **Variables and Scope**.
   - The §"See also" link to **Error Handling** (`/fleans/guides/error-handling/`).
   - The §"See also" link to **BPMN Support** (`/fleans/concepts/bpmn-support/`).

8. **Drift-guard freshness.** Open
   `website/src/content/docs/guides/message-correlation.md` in the repo. The
   HTML drift-guard comment at the top pins line ranges against branch SHA
   `b7d80af`. For each pin, run:
   ```bash
   # ResolveCorrelationKey
   sed -n '2778,2790p' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs
   # ProcessRegisterMessage
   sed -n '989,1011p' src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs
   # BpmnConverter zeebe:subscription parse
   sed -n '895,925p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
   # WorkflowController SendMessage
   sed -n '50,65p' src/Fleans/Fleans.Api/Controllers/WorkflowController.cs
   # SendMessageRequest DTO
   sed -n '5p' src/Fleans/Fleans.ServiceDefaults/DTOs/SendMessageRequest.cs
   # Fixtures
   ls tests/manual/09-message-events/message-catch.bpmn
   ls tests/manual/16-message-start-event/message-start-event.bpmn
   ls tests/manual/21-event-subprocess-message/message-event-subprocess.bpmn
   ```
   Each excerpt must reference the symbol named in the pin (e.g. line 2778 must
   contain `private string ResolveCorrelationKey`, line 989 must contain
   `private SubscribeMessageEffect ProcessRegisterMessage`, line 895 must be
   inside the `messages.Add` block, line 50 must contain
   `[HttpPost("message", Name = "SendMessage")]`).

   If any pin is stale, **regenerate the drift-guard block at the current SHA
   and update the citations in the guide accordingly** — do not leave drift in
   place.

9. **Production build sanity** (final pass after step 1, repeated once you've
   made any drift fixes from step 8).
   - `Ctrl+C` the dev server.
   - `npm run build` from `website/` exits 0.

## Expected outcomes (checklist)

- [ ] `npm run build` exits 0; `dist/guides/message-correlation/index.html` exists.
- [ ] Light-theme render: page + sidebar entry + all `:::caution` admonitions
      render correctly.
- [ ] Dark-theme render: same, with no contrast regressions.
- [ ] All three fixture file paths (#09, #16, #21) are referenced by name in
      the rendered guide.
- [ ] Seven `:::caution` admonitions render (one above §"Common pitfalls", five
      inside §"Common pitfalls", one inside §"Limitations").
- [ ] The end-to-end curl block renders as a single fenced bash block with
      three `curl` invocations.
- [ ] All four cross-links (Variables and Scope ×2, Error Handling, BPMN
      Support) return HTTP 200.
- [ ] Every drift-guard pin (`WorkflowExecution.cs:2778-2790`,
      `WorkflowExecution.cs:989-1011`, `BpmnConverter.cs:895-925`,
      `WorkflowController.cs:50-65`, `SendMessageRequest.cs:5`,
      and the three fixture paths) still resolves to the named symbol at the
      current branch SHA.
