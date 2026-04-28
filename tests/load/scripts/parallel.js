// tests/load/scripts/parallel.js
//
// Scenario 2 — parallel branching: ramp to 500 VUs, measuring workflow start latency
// against a 3-branch fork/join fixture.
//
// TARGET INFRASTRUCTURE: cloud / multi-silo Docker Compose (see issue #237, issue #244).
// Do NOT run at full VU count against a local single-node dev setup — it will overwhelm
// the local database. For local connectivity checks use:
//   k6 run --vus 5 --iterations 20 --insecure-skip-tls-verify tests/load/scripts/parallel.js
//
// Prerequisites:
//   1. Target cluster running
//   2. Fixtures deployed: k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js
//
// Standalone:  k6 run --insecure-skip-tls-verify tests/load/scripts/parallel.js
// Mixed run:   imported by mixed.js (issue #242) via the parallelWorkflow named export
//
// METRIC SCOPE: workflowStartDuration captures the synchronous latency of POST /Workflow/start
// (instance creation), not fork/join completion. The fork/join coordination runs asynchronously
// inside Orleans after the 200 is returned. Comparing this scenario to linear.js at equal VU
// counts measures activation overhead under different downstream processing loads — parallel
// instances spawn 3 concurrent grain activations per start, which can back-pressure the silo
// scheduler and indirectly raise start latency. End-to-end fork/join completion timing is
// out of scope here and is covered by cloud validation (issue #244).
//
// FIXTURE: tests/load/fixtures/parallel-workflow.bpmn (PR #323) — process id "load-parallel",
// a 3-branch fork/join (branchA, branchB, branchC), each branch a single C# scriptTask.

import http             from 'k6/http';
import { check, sleep } from 'k6';
import { workflowStartDuration } from './metrics.js';
const thresholds = JSON.parse(open('../thresholds.json'));

const BASE_URL = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS  = { 'Content-Type': 'application/json' };

export const options = {
  stages: [
    { duration: '1m', target: 500 },  // ramp up
    { duration: '3m', target: 500 },  // sustained load
    { duration: '1m', target:   0 },  // cool-down
  ],
  insecureSkipTLSVerify: true,
  thresholds: {
    ...thresholds,
  },
};

// Named export required by mixed.js (exec: 'parallelWorkflow')
export function parallelWorkflow() {
  const payload = JSON.stringify({ WorkflowId: 'load-parallel' });
  const res = http.post(`${BASE_URL}/Workflow/start`, payload, { headers: HEADERS });

  workflowStartDuration.add(res.timings.duration);
  check(res, { 'workflow start: status 200': (r) => r.status === 200 });

  // 100ms think-time: paces each VU at ~10 req/s, matching linear.js so the two
  // scenarios are directly comparable at the same VU count. At 500 VUs this yields
  // ~5 000 starts/s sustained throughput — each start spawning a 3-branch fork/join,
  // which exercises grain-scheduler contention without changing the request rate.
  sleep(0.1);
}

// Default export for standalone k6 runs
export default parallelWorkflow;
