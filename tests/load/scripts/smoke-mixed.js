// tests/load/scripts/smoke-mixed.js
//
// Smoke test variant of the mixed-workload scenario.
//
// PURPOSE
// -------
// A shorter validation run (≈2 minutes total) for quick checks after deployments
// or code changes. Uses the same three workflow functions as mixed.js but with
// reduced VU counts and shortened stage durations.
//
// VU COUNTS: 5 per-scenario = 15 total across all three scenarios.
//
// WHY A SEPARATE FILE?
// --------------------
// When options.scenarios defines named scenarios, the --stage CLI flag is silently
// ignored by k6 (or conflicts by implicitly creating a default scenario). The named
// scenarios always run with the stages declared in the script regardless of CLI flags.
// smoke-mixed.js solves this by declaring shortened stages explicitly in code.
//
// PREREQUISITES
// -------------
// Same as mixed.js — all BPMN fixtures must be deployed first:
//   k6 run tests/load/scripts/setup.js
// Verify all fixtures report `deployed: true` before proceeding.
//
// PER-SCENARIO METRIC TAGS
// ------------------------
// k6 tags all metrics with the scenario name (e.g., scenario:linear_smoke).
// Use --out json=smoke-results.json to capture output and filter by tags.scenario.

import { linearWorkflow }      from './linear.js';
import { parallelWorkflow }    from './parallel.js';
import { eventDrivenWorkflow } from './events.js';

export const options = {
  scenarios: {
    linear_smoke: {
      executor:     'ramping-vus',
      exec:         'linearWorkflow',
      startVUs:     0,
      gracefulStop: '10s',
      stages: [
        { duration: '30s', target: 5 },  // 5 VUs for this scenario
        { duration: '1m',  target: 5 },
        { duration: '30s', target: 0 },
      ],
    },

    parallel_smoke: {
      executor:     'ramping-vus',
      exec:         'parallelWorkflow',
      startVUs:     0,
      gracefulStop: '10s',
      stages: [
        { duration: '30s', target: 5 },  // 5 VUs for this scenario
        { duration: '1m',  target: 5 },
        { duration: '30s', target: 0 },
      ],
    },

    events_smoke: {
      executor:     'ramping-vus',
      exec:         'eventDrivenWorkflow',
      startVUs:     0,
      gracefulStop: '10s',
      stages: [
        { duration: '30s', target: 5 },  // 5 VUs for this scenario
        { duration: '1m',  target: 5 },
        { duration: '30s', target: 0 },
      ],
    },
  },

  thresholds: {
    'http_req_failed':         ['rate<0.01'],
    'http_req_duration':       ['p(95)<2000'],
    'workflow_start_duration': ['p(95)<3000'],
  },
};

export { linearWorkflow, parallelWorkflow, eventDrivenWorkflow };
