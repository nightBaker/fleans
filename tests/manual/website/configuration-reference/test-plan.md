# Manual Test Plan — Configuration reference (Issue #403)

Verifies that the new `reference/configuration.md` page renders with both tiers, that all per-host auth applicability rows are accurate, that all 8 drift-guard source pins still resolve, and that cross-links from the 5 referrer pages reach the right anchors.

## Prerequisites

- `cd website && npm install` has been run at least once.
- Dev server NOT already running on port 4321.

## Steps

### 1. Build passes

```bash
cd website
npm run build
```

**Expect:** zero errors, page emitted to `dist/reference/configuration/index.html`.

### 2. Naming-rule paragraph rendered

```bash
F=website/dist/reference/configuration/index.html
grep -c 'EnvironmentVariablesConfigurationProvider' $F   # ≥ 1 (cross-link to MS Learn)
grep -c 'each .: in a config key becomes' $F             # ≥ 1 (the rule)
```

**Expect:** both ≥ 1.

### 3. Tier 1 (Aspire / SQLite-mode dev knobs) table rendered

```bash
F=website/dist/reference/configuration/index.html
grep -c 'FLEANS_PERSISTENCE_PROVIDER' $F                 # ≥ 1
grep -c 'FLEANS_STREAMING_PROVIDER' $F                   # ≥ 1
grep -c 'FLEANS_SQLITE_CONNECTION' $F                    # ≥ 1
grep -c 'FLEANS_QUERY_CONNECTION' $F                     # ≥ 1
grep -c 'FLEANS_PG_TESTS' $F                             # ≥ 1
```

**Expect:** all 5 ≥ 1. Visually confirm the Tier-1 table has 5 rows.

### 4. Tier 2 runtime-keys table — 13 rows including the 4 split auth rows

```bash
F=website/dist/reference/configuration/index.html
grep -c 'Fleans__Role' $F                                # ≥ 1
grep -c 'Persistence__Provider' $F                       # ≥ 1
grep -c 'Fleans__Streaming__Provider' $F                 # ≥ 1
grep -c 'Fleans__Streaming__Kafka__Brokers' $F           # ≥ 1
grep -c 'Authentication__Authority' $F                   # ≥ 1
grep -c 'Authentication__Audience' $F                    # ≥ 1
grep -c 'Authentication__ClientId' $F                    # ≥ 1
grep -c 'Authentication__ClientSecret' $F                # ≥ 1
grep -c 'ConnectionStrings__fleans' $F                   # ≥ 1
grep -c 'ConnectionStrings__fleans-query' $F             # ≥ 1
grep -c 'ConnectionStrings__orleans-redis' $F            # ≥ 1
```

**Expect:** all ≥ 1. Visually confirm the Tier-2 tables (Role, Persistence, Streaming, Authentication, ConnectionStrings) total 13 distinct rows.

### 5. Auth per-host applicability rendered correctly

```bash
F=website/dist/reference/configuration/index.html
# "API only" appears for Audience, "Web only" appears for ClientId + ClientSecret, "Both hosts" for Authority
grep -cE 'API only|Web only|Both hosts' $F               # ≥ 4 (Audience=API only, ClientId=Web only, ClientSecret=Web only, Authority=Both hosts)
```

**Expect:** ≥ 4. Visually inspect the auth table — confirm that Audience says "API only", ClientId/ClientSecret say "Web only", Authority says "Both hosts".

### 6. Drift-guard source pins still resolve

```bash
grep -cE 'Authentication:Authority|Authentication:Audience' src/Fleans/Fleans.Api/Program.cs        # ≥ 2
grep -cE 'Authentication:ClientId|Authentication:ClientSecret' src/Fleans/Fleans.Web/Program.cs     # ≥ 2
grep -cE 'Fleans:Role' src/Fleans/Fleans.Api/Program.cs src/Fleans/Fleans.WorkerHost/Program.cs src/Fleans/Fleans.CustomWorkerHost/Program.cs  # ≥ 3
grep -cE 'Persistence:Provider' src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs    # ≥ 1
grep -cE 'Fleans:Streaming:Provider|Fleans:Streaming:Kafka' src/Fleans/Fleans.ServiceDefaults/FleanStreamingExtensions.cs  # ≥ 2
grep -cE 'public string Brokers' src/Fleans/Fleans.Streaming.Kafka/KafkaStreamingOptions.cs         # ≥ 1
grep -cE 'GetConnectionString\("orleans-redis"\)' src/Fleans/Fleans.Api/Program.cs src/Fleans/Fleans.WorkerHost/Program.cs src/Fleans/Fleans.CustomWorkerHost/Program.cs  # ≥ 3
grep -cE 'GetConnectionString\("fleans"\)|GetConnectionString\("fleans-query"\)' src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs  # ≥ 2
grep -cE 'FLEANS_PERSISTENCE_PROVIDER|FLEANS_STREAMING_PROVIDER|FLEANS_SQLITE_CONNECTION|FLEANS_QUERY_CONNECTION' src/Fleans/Fleans.Aspire/Program.cs src/Fleans/Fleans.ServiceDefaults/FleansPersistenceExtensions.cs  # ≥ 4
```

**Expect:** every count meets its threshold. If any drops below, the doc claim has drifted from source — fix the doc OR fix the source link.

### 7. Cross-links from 5 referrer pages resolve

```bash
F=website
grep -c 'reference/configuration' $F/dist/concepts/architecture/index.html   # ≥ 1
grep -c 'reference/configuration' $F/dist/reference/deployment/index.html    # ≥ 1
grep -c 'reference/configuration' $F/dist/reference/persistence/index.html   # ≥ 1
grep -c 'reference/configuration' $F/dist/reference/streaming/index.html     # ≥ 1
grep -c 'reference/configuration' $F/dist/guides/quick-start/index.html      # ≥ 1
```

**Expect:** all 5 ≥ 1.

### 8. Sidebar order — Configuration first in Reference group

`cd website && npm run dev`, then visit `https://localhost:4321/fleans/reference/configuration/`. In the left sidebar, **Configuration** must be the first entry under the "Reference" heading (above Deployment, Persistence, Streaming, etc.).

### 9. `FLEANS_STANDALONE` phantom removed

```bash
grep -c 'FLEANS_STANDALONE' website/src/content/docs/concepts/architecture.md  # 0 expected
grep -rc 'FLEANS_STANDALONE' src/Fleans/ --include="*.cs" 2>/dev/null | grep -v ':0$' | wc -l  # 0 expected (no source uses it)
```

**Expect:** both 0. The line at `architecture.md:77` should now reference `ConnectionStrings__orleans-redis` (or similar verified value), not the phantom `FLEANS_STANDALONE`.

### 10. Both themes render

`cd website && npm run dev`, then toggle light/dark via the navbar:

- Visit `/fleans/reference/configuration/` — all 6 tables (Tier-1, Role, Persistence, Streaming, Authentication, ConnectionStrings, .NET runtime) are readable in both themes.

## Verdict

- **PASSED** — all 10 steps green. Move PR to Review by Human.
- **FAILED / BUG** — file follow-up issue, send PR back to Ready.
