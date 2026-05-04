# Manual Test Plan — BPMN Support coverage matrix (Issue #404)

Verifies that the new BPMN coverage matrix on `concepts/bpmn-support.md` renders correctly with all 54 per-variant rows, source pins resolve to `BpmnConverter.cs`, and README's deference is replaced with a 3-line cross-reference.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors, page emitted to `dist/concepts/bpmn-support/index.html`.

### 2. Status legend rendered (4 emoji + meanings)

```bash
F=website/dist/concepts/bpmn-support/index.html
grep -c '✅' $F   # ≥ 1 (legend + multiple ✅ rows)
grep -c '⚠️' $F   # ≥ 1 (legend + ⚠️ rows)
grep -c '🚧' $F   # ≥ 1 (legend mention)
grep -c '❌' $F   # ≥ 1 (legend + ❌ rows for Pool/Lane/etc.)
grep -c 'engine support; editor UI is a separate concern' $F  # ≥ 1 (⚠️ clarifier)
```

**Expect:** all counts ≥ 1.

### 3. Coverage matrix has 10 sub-tables and 54 variant rows total

```bash
F=website/dist/concepts/bpmn-support/index.html
# Each sub-table has an h3 heading
grep -cE 'id="start-events"|id="intermediate-catch-events"|id="intermediate-throw-events"|id="end-events"|id="boundary-events"|id="tasks"|id="sub-processes"|id="gateways"|id="connecting-objects"|id="swimlanes-and-artifacts"' $F
```

**Expect:** ≥ 10 (one per sub-table heading). Visually inspect: row counts should be Start 7 / Catch 5 / Throw 4 / End 5 / Boundary 9 / Tasks 5 / Sub-Process 4 / Gateways 5 / Connecting 4 / Swimlanes-Artifacts 6 = 54 total.

Spot-check key per-variant row labels appear (means the matrix renders the granularity it promised — not collapsed into parent rows):

```bash
F=website/dist/concepts/bpmn-support/index.html
for label in 'Cancel Boundary' 'Multiple Start Event' 'Escalation Intermediate Throw' \
             'Compensation End Event' 'Conditional Boundary' 'Cancel End Event' \
             'Error Start Event'; do
  echo "  '$label': $(grep -c "$label" $F)"   # each ≥ 1
done
```

**Expect:** every count ≥ 1. If any is 0, the matrix is missing that variant row.

### 4. Drift-guard pins still resolve in `BpmnConverter.cs`

```bash
F=src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
# 18 parent foreach handlers
grep -cE 'foreach.*Bpmn \+ "(startEvent|intermediateCatchEvent|intermediateThrowEvent|endEvent|task|userTask|serviceTask|scriptTask|exclusiveGateway|parallelGateway|inclusiveGateway|complexGateway|eventBasedGateway|subProcess|transaction|callActivity|boundaryEvent)"|^\s+private void ParseSequenceFlows' $F
# Child event-definition + loop detection branches
grep -cE '\.Element\(Bpmn \+ "(timerEventDefinition|messageEventDefinition|signalEventDefinition|errorEventDefinition|conditionalEventDefinition|cancelEventDefinition|compensateEventDefinition|escalationEventDefinition|multiInstanceLoopCharacteristics|loopCardinality)"' $F
# triggeredByEvent attribute
grep -cE 'triggeredByEvent' $F
```

**Expect:** parent-handler grep ≥ 18, child-detection grep ≥ 14, `triggeredByEvent` ≥ 2. If any pin disappears, the `BpmnConverter.cs` parse logic has changed and the matrix needs auditing.

### 5. Spot-check 5 random Source-pin links resolve

Pick 5 random rows from the rendered matrix; for each, confirm the cited `BpmnConverter.cs:NNN` line is the parse-handler / detection branch the row claims:

```bash
sed -n '94p;303p;574p;638p;845p' src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs
```

**Expect:** lines say `foreach (var startEvent in scopeElement.Elements(Bpmn + "startEvent"))`, `... "task"`, `... "transaction"`, `... "boundaryEvent"`, `private void ParseSequenceFlows(...)` respectively.

### 6. Spot-check 5 random Tested-by links resolve

Pick 5 random `tests/manual/NN-*/` paths cited in the matrix; for each, confirm the directory exists:

```bash
for fixture in 01-basic-workflow 13-multi-instance 24-conditional-event 30-cancel-event 37-custom-task-framework; do
  [ -d "tests/manual/$fixture" ] && echo "✓ $fixture" || echo "✗ MISSING: $fixture"
done
```

**Expect:** all 5 ✓.

### 7. README cross-reference rendered

```bash
grep -cE '## BPMN coverage' README.md         # 1
grep -cE 'nightbaker.github.io/fleans/concepts/bpmn-support' README.md  # 1
grep -cE 'For now, next elements are implemented' README.md  # 0 (old wording removed)
```

**Expect:** first 2 = 1, last = 0. Visit `https://nightbaker.github.io/fleans/concepts/bpmn-support/` after deploy and confirm the link target loads.

### 8. `bpmn-support.md` deference removed

```bash
grep -c 'See the project `README.md` for the authoritative' website/src/content/docs/concepts/bpmn-support.md  # 0 expected
```

**Expect:** 0. The old deference line is gone.

### 9. Mobile-table rendering (manual)

`cd website && npm run dev`, then:
1. Visit `https://localhost:4321/fleans/concepts/bpmn-support/` in Chrome DevTools mobile-emulation mode (≤ 480 px width).
2. Each of the 10 sub-tables should remain readable — column widths should not crush text into single characters per line, and horizontal scroll should be available if needed.

### 10. Both themes render (deferred to maintainer)

`cd website && npm run dev`, toggle light/dark via the navbar:
- Visit `/fleans/concepts/bpmn-support/` — all 10 sub-tables readable in both themes; emoji status icons render correctly (no boxes).

## Verdict

- **PASSED** — all 10 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
