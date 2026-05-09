# Manual Test Plan — Orleans Dashboard documentation (Issue #402)

Verifies that the enriched `### Orleans Dashboard` subsection in `reference/observability.md` renders correctly, that all cross-links resolve, and that the four drift-guard source pins still resolve to the named symbols at the merging-PR's SHA.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors, page emitted to `dist/reference/observability/index.html`.

### 2. New subsection content rendered

```bash
F=website/dist/reference/observability/index.html
grep -c 'What it shows' $F                       # ≥ 1 (the new h4)
grep -c 'Microsoft.Orleans.Dashboard' $F          # ≥ 1 (package name anchor)
grep -c '/dashboard' $F                           # multiple (route + cross-references)
grep -c 'TimerCallbackGrain' $F                   # ≥ 1 (Reminders-page evidence)
```

**Expect:** all counts ≥ 1.

### 3. OTLP caveat correctly inverted

```bash
grep -c 'is.*instrumented' website/dist/reference/observability/index.html
```

**Expect:** ≥ 1. Verify visually that the operational-caveats paragraph reads *"The dashboard's HTTP traffic IS instrumented..."* (not the v1's wrong "does NOT").

### 4. Authentication cross-link rendered

```bash
grep -c 'authentication/#behaviour-when-enabled' website/dist/reference/observability/index.html
```

**Expect:** ≥ 1. In dev-server mode, click the link and confirm it scrolls to the right section on `/fleans/reference/authentication/`.

### 5. Quick Start cross-link rendered

```bash
grep -c 'observability/#orleans-dashboard' website/dist/guides/quick-start/index.html
grep -c 'is the workflow engine alive' website/dist/guides/quick-start/index.html
```

**Expect:** both ≥ 1. In dev-server mode, click the inline italic tip from `/fleans/guides/quick-start/` and confirm it scrolls to the new subsection at `/fleans/reference/observability/#orleans-dashboard`.

### 6. Drift-guard pins still resolve

```bash
grep -c 'MapOrleansDashboard' src/Fleans/Fleans.Web/Program.cs
grep -cE 'IRemindable|RegisterOrUpdateReminder' src/Fleans/Fleans.Application/Grains/TimerCallbackGrain.cs
grep -cE 'AddAspNetCoreInstrumentation' src/Fleans/Fleans.ServiceDefaults/Extensions.cs
```

**Expect:**
- `MapOrleansDashboard` ≥ 1 (Web/Program.cs:200).
- `IRemindable` + `RegisterOrUpdateReminder` ≥ 3 combined (TimerCallbackGrain.cs:8 declares the interface; :22, :79 call `RegisterOrUpdateReminder`).
- `AddAspNetCoreInstrumentation` ≥ 2 (Extensions.cs:55 metrics + :65 tracing).

If any of the above changes (e.g., a `Filter()` lambda gets added that excludes `/dashboard`), the OTLP caveat is wrong again and needs editing.

### 7. Both themes render

`cd website && npm run dev`, then toggle light/dark via the navbar:

- Visit `/fleans/reference/observability/#orleans-dashboard` — table renders with 4 rows readable in both themes.
- Visit `/fleans/guides/quick-start/` — italic tip renders styled correctly in both themes.

## Verdict

- **PASSED** — all 7 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
