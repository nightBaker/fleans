# Cloud Load Test Results — Phase 3

> Fill in this table after completing each phase. See `tests/load/cloud-setup.md` for the full playbook.

> **Known limitation:** Domain event counts in logs will be lower than total workflow count in multi-silo runs due to memory stream silo-scoping. This does not affect k6 throughput metrics.

## Phase 3A — Baseline Confirmation (2 silos, cloud Linux)

| Scenario | VUs | Host | RPS | p50 (ms) | p95 (ms) | Error % | Saturated? |
|----------|-----|------|-----|----------|----------|---------|------------|
| linear   | … | cloud-linux (2 silos) | … | … | … | … | No |
| parallel | … | cloud-linux (2 silos) | … | … | … | … | No |
| events   | … | cloud-linux (2 silos) | … | … | … | … | No |
| mixed    | … | cloud-linux (2 silos) | … | … | … | … | No |

## Phase 3B — Saturation Search (2 silos)

| VU stage | RPS | p95 (ms) | Error % | Saturated? |
|----------|-----|----------|---------|------------|
| 50       | … | … | … | No |
| 100      | … | … | … | No |
| 200      | … | … | … | No |
| 500      | … | … | … | … |
| 1000     | … | … | … | … |
| 2000     | … | … | … | … |

**2-silo saturation point:** …VUs

## Phase 3C — Horizontal Scaling (4 silos)

| VU stage | RPS | p95 (ms) | Error % | Saturated? |
|----------|-----|----------|---------|------------|
| …        | … | … | … | … |

**4-silo saturation point:** …VUs

**Horizontal scaling factor:** (4-silo RPS at saturation) / (2-silo RPS at saturation) = …× (ideal: 2×)

## Phase 3D — Mixed Workload at Scale

| Scenario | VUs | RPS | p95 (ms) | Error % |
|----------|-----|-----|----------|---------|
| mixed    | … | … | … | … |

## Mac vs Linux Comparison

| Scenario | VUs | Host | RPS | p50 (ms) | p95 (ms) | Error % |
|----------|-----|------|-----|----------|----------|---------|
| linear | … | local-mac (2 silos) | … | … | … | … |
| linear | … | cloud-linux (2 silos) | … | … | … | … |

## Bottleneck Analysis

_Fill in after reviewing Grafana dashboards and compose logs._

- CPU: …
- PostgreSQL connections: …
- Redis: …
- Orleans grain activation: …

## Recommendations

_Fill in after analysis._
