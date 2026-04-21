// tests/load/scripts/mixed.js
//
// Mixed-workload k6 scenario that runs all three workflow patterns in parallel.
//
// PURPOSE
// -------
// The three individual scenario scripts (linear.js, parallel.js, events.js) test
// each workflow pattern in isolation. This script composes them into a single run
// so their workloads overlap, revealing system-level bottlenecks that only emerge
// under heterogeneous load: database write contention across workflow types,
// Redis cluster pressure from concurrent grain activations, and scheduler/thread
// competition between CPU-bound and IO-bound paths.
//
// PREREQUISITES
// -------------
// 1. All BPMN fixtures must be deployed before this script runs:
//      k6 run tests/load/scripts/setup.js
//    Verify that all fixtures report `deployed: true` before proceeding.
//    If fixtures are not deployed, POST /Workflow/start calls will return errors
//    and the run results will be meaningless.
//
// 2. Dependency scripts must satisfy the contract in docs/plans/2026-04-19-mixed-load-scenario-design.md:
//    - linear.js  : exports `linearWorkflow`, imports workflowStartDuration from ./metrics.js
//    - parallel.js: exports `parallelWorkflow`, imports workflowStartDuration from ./metrics.js
//    - events.js  : exports `eventDrivenWorkflow`, imports workflowStartDuration from ./metrics.js
//    - None of the above may rely on their own setup()/teardown() for state that
//      their named export function depends on.
//
// PER-SCENARIO METRIC TAGS
// ------------------------
// k6 automatically tags all emitted metrics with the scenario name (e.g., scenario:linear_40pct).
// Use these tags to isolate bottlenecks per workload type when analyzing results:
//   k6 run --out json=full-run-results.json tests/load/scripts/mixed.js
// Then filter by tags.scenario in the JSON output or a Grafana dashboard.
//
// THRESHOLD NOTE
// --------------
// Once tests/load/thresholds.json exists (created by #239), verify that
// http_req_failed and http_req_duration values here align with or intentionally
// diverge from the shared values. The workflow_start_duration threshold (3000ms)
// is intentionally relaxed vs. the isolation baseline (tighter) to account for
// cross-workload contention under 100 concurrent VUs.

import { linearWorkflow }      from './linear.js';
import { parallelWorkflow }    from './parallel.js';
import { eventDrivenWorkflow } from './events.js';

export const options = {
  scenarios: {
    // 40% of peak VUs — linear workflow generates the highest write pressure
    // (one DB write per activity completion, no waiting), so it gets the
    // largest share to maximize write contention visibility.
    linear_40pct: {
      executor:     'ramping-vus',
      exec:         'linearWorkflow',
      startVUs:     0,
      gracefulStop: '30s',
      stages: [
        { duration: '1m', target: 40 },
        { duration: '3m', target: 40 },
        { duration: '1m', target:  0 },
      ],
    },

    // 30% of peak VUs — parallel workflow involves internal fork/join coordination;
    // less frequent in typical workloads but important to cover under concurrent load.
    parallel_30pct: {
      executor:     'ramping-vus',
      exec:         'parallelWorkflow',
      startVUs:     0,
      gracefulStop: '30s',
      stages: [
        { duration: '1m', target: 30 },
        { duration: '3m', target: 30 },
        { duration: '1m', target:  0 },
      ],
    },

    // 30% of peak VUs — event-driven workflow involves more internal coordination
    // (message delivery, signal routing); covered at 30% to reveal IO-bound contention.
    events_30pct: {
      executor:     'ramping-vus',
      exec:         'eventDrivenWorkflow',
      startVUs:     0,
      gracefulStop: '30s',
      stages: [
        { duration: '1m', target: 30 },
        { duration: '3m', target: 30 },
        { duration: '1m', target:  0 },
      ],
    },
  },

  thresholds: {
    // Align with thresholds.json from #239 once that file exists.
    'http_req_failed':   ['rate<0.01'],
    'http_req_duration': ['p(95)<2000'],

    // Intentionally relaxed vs. isolation baseline to account for contention
    // from 100 concurrent VUs across three workflow types.
    // REQUIRES: all three dependency scripts emit workflowStartDuration.add(res.timings.duration)
    // for their POST /Workflow/start call (via import from ./metrics.js).
    // Silently skipped by k6 if the metric is never emitted.
    'workflow_start_duration': ['p(95)<3000'],
  },
};

// k6 requires exec: functions to be exported from the same file that declares options.
// The re-exports below satisfy that requirement. The default export (for standalone
// runs of each dependency script) is unaffected.
export { linearWorkflow, parallelWorkflow, eventDrivenWorkflow };
