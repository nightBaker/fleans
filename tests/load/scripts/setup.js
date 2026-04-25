// tests/load/scripts/setup.js
//
// Deploys all 3 load-test BPMN fixtures to the target cluster before any
// k6 scenario script runs. Run once before linear.js / parallel.js / events.js / mixed.js:
//
//   k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js
//
// Prerequisites: target cluster running with POST /Workflow/deploy endpoint available.
// Abort on any fixture deploy failure — do NOT continue with partial setup.

import http            from 'k6/http';
import { check, fail } from 'k6';

// Fixtures are read at init time (k6 open() runs before VUs start).
const linearBpmn   = open('../fixtures/linear-workflow.bpmn');
const parallelBpmn = open('../fixtures/parallel-workflow.bpmn');
const eventsBpmn   = open('../fixtures/events-workflow.bpmn');

const BASE_URL = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS  = { 'Content-Type': 'application/json' };

const FIXTURES = [
  { name: 'load-linear',   bpmnXml: linearBpmn   },
  { name: 'load-parallel', bpmnXml: parallelBpmn },
  { name: 'load-events',   bpmnXml: eventsBpmn   },
];

export const options = {
  vus: 1,
  iterations: 1,
  insecureSkipTLSVerify: true,   // local dev uses self-signed cert
  thresholds: {
    // Only HTTP failure rate is relevant for setup — no workflow_start_duration emitted here.
    'http_req_failed': ['rate<0.01'],
  },
};

export default function () {
  // 1. Deploy each fixture — abort immediately on failure
  for (const fixture of FIXTURES) {
    const payload = JSON.stringify({ BpmnXml: fixture.bpmnXml });
    const res = http.post(`${BASE_URL}/Workflow/deploy`, payload, { headers: HEADERS });

    if (res.status !== 200) {
      fail(`Failed to deploy ${fixture.name}: HTTP ${res.status} — ${res.body}`);
    }

    const body = res.json();
    console.log(`Deployed ${fixture.name} v${body.Version}`);
  }

  // 2. Verify all 3 keys appear in the definitions list.
  //    Pass pageSize=100 to avoid missing fixtures when >20 definitions exist.
  const defsRes = http.get(`${BASE_URL}/Workflow/definitions?pageSize=100`);
  check(defsRes, { 'definitions list: status 200': (r) => r.status === 200 });

  if (defsRes.status !== 200) {
    fail(`Cannot verify fixtures: GET /Workflow/definitions returned HTTP ${defsRes.status}`);
  }

  // Response shape: PagedResult<ProcessDefinitionSummary>
  //   { Items: [{ ProcessDefinitionKey, Version, ... }], TotalCount, Page, PageSize }
  const keys = defsRes.json('Items').map((d) => d.ProcessDefinitionKey);

  for (const fixture of FIXTURES) {
    const found = keys.some((k) => k === fixture.name);
    check({ found }, { [`${fixture.name} present in definitions`]: (x) => x.found });
    if (!found) {
      fail(`MISSING: ${fixture.name} not found in /Workflow/definitions — cannot proceed`);
    }
  }
}
