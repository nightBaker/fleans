// tests/load/scripts/linear.js
//
// Scenario 1 — linear throughput: ramp to 500 VUs, measuring workflow start latency.
//
// TARGET INFRASTRUCTURE: cloud / multi-silo Docker Compose (see issue #237, issue #244).
// Do NOT run at full VU count against a local single-node dev setup — it will overwhelm
// the local database. For local connectivity checks use:
//   k6 run --vus 5 --iterations 20 --insecure-skip-tls-verify tests/load/scripts/linear.js
//
// Prerequisites:
//   1. Target cluster running
//   2. Fixtures deployed: k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js
//
// Standalone:  k6 run --insecure-skip-tls-verify tests/load/scripts/linear.js
// Mixed run:   imported by mixed.js (issue #242) via the linearWorkflow named export

import http             from 'k6/http';
import { check, sleep } from 'k6';
import { workflowStartDuration } from './metrics.js';
import thresholds       from '../thresholds.json';

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

// Named export required by mixed.js (exec: 'linearWorkflow')
export function linearWorkflow() {
  const payload = JSON.stringify({ WorkflowId: 'load-linear' });
  const res = http.post(`${BASE_URL}/Workflow/start`, payload, { headers: HEADERS });

  workflowStartDuration.add(res.timings.duration);
  check(res, { 'workflow start: status 200': (r) => r.status === 200 });

  // 100ms think-time: paces each VU at ~10 req/s, giving the server realistic
  // request spacing. At 500 VUs this yields ~5 000 starts/s sustained throughput —
  // enough to stress the system while leaving headroom for async instance processing.
  sleep(0.1);
}

// Default export for standalone k6 runs
export default linearWorkflow;
