// tests/load/scripts/long-lived.js
//
// Scenario long-lived (#973, roadmap "How it is measured"): measure linear-10's start rate on an
// empty cluster, park K6_PARK_COUNT instances on a message catch (default 10 000 000), then measure
// it again with all of them waiting. Pass: the start rate with the parked instances drops by no
// more than 10 %, and no request fails.
//
// The start rate is a closed model: K6_LINEAR_VUS VUs start linear-10 back to back for K6_WINDOW_S,
// so the number of accepted starts per window is the rate the cluster sustains. Both windows have
// the same length and VU count; the summary prints their starts/s and the drop. At saturation the
// stack sheds some starts with 429: they are expected (not failed requests) and counted apart as
// linear_rejected, so the comparison is accepted starts against accepted starts.
//
// Timeline (all scenarios in one run):
//   linear_warmup  linear-10 starts for K6_WARMUP_S, not measured (JIT, pools, grain activation)
//   linear_empty   linear-10 starts, K6_LINEAR_VUS VUs for K6_WINDOW_S
//   park           constant-arrival-rate starts of load-long-lived at K6_PARK_RATE/s, after K6_SETTLE_S
//   linear_loaded  linear-10 starts again, K6_SETTLE_S after the last parked start
// Then run scripts/long-lived.sql against the `fleans` database for parked / waiting and linear-10's
// starts, completions and duration p99 per window. Use a FRESH stack per run (`docker compose down -v`):
// the query reads every load-long-lived and load-linear-10 instance in the database.
//
// Start the stack with overrides/signal-fanout.yml: the read-model projector keeps one activation per
// waiting instance, and the default 512 MB worker runs out of memory from ~10 000 of them (#1011).
//
//   K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/long-lived.js
//   K6_TARGET_URL=http://localhost:80 K6_PARK_COUNT=20000 K6_PARK_RATE=150 k6 run tests/load/scripts/long-lived.js   # laptop

import http from 'k6/http';
import { check, fail } from 'k6';
import { Counter } from 'k6/metrics';
import exec from 'k6/execution';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { workflowStartDuration } from './metrics.js';

const longLivedBpmn = open('../fixtures/long-lived-workflow.bpmn');
const linear10Bpmn  = open('../fixtures/linear-10-workflow.bpmn');
// The warm-up starts a copy under its own key, so scripts/long-lived.sql leaves it out of the 'empty' window.
const warmupBpmn    = linear10Bpmn.replace('id="load-linear-10"', 'id="load-linear-10-warmup"');

const BASE_URL = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS  = { 'Content-Type': 'application/json' };

function positiveInt(name, fallback) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return fallback;
  const n = Number(raw);
  if (!Number.isInteger(n) || n <= 0) throw new Error(`${name} must be a positive integer (got '${raw}')`);
  return n;
}

const COUNT       = positiveInt('K6_PARK_COUNT', 10000000);
const RATE        = positiveInt('K6_PARK_RATE', 1000);    // parked starts per second
const LINEAR_VUS  = positiveInt('K6_LINEAR_VUS', 20);     // linear-10 VUs per window
const WINDOW_S    = positiveInt('K6_WINDOW_S', 120);      // length of each linear-10 window
const WARMUP_S    = positiveInt('K6_WARMUP_S', 60);       // unmeasured linear-10 before the empty window
const SETTLE_S    = positiveInt('K6_SETTLE_S', 30);       // pause between the phases, to drain
const MAX_VUS     = positiveInt('K6_PARK_MAX_VUS', 1000);
const PARK_MS     = Math.ceil(COUNT * 1000 / RATE);       // RATE/s for this long ≈ COUNT starts
const PARK_S      = Math.ceil(PARK_MS / 1000);
const EMPTY_AT    = WARMUP_S;
const PARK_AT     = EMPTY_AT + WINDOW_S + SETTLE_S;
const LOADED_AT   = PARK_AT + PARK_S + SETTLE_S;

const parked       = new Counter('long_lived_parked');
const linearStarts = new Counter('linear_starts');        // accepted linear-10 starts, per window
const linearShed   = new Counter('linear_rejected');      // linear-10 starts answered 429, per window
const linearParams = { headers: HEADERS, responseCallback: http.expectedStatuses(200, 429) };

const linearScenario = (startS, durationS = WINDOW_S) => ({
  executor: 'constant-vus',
  exec: 'startLinear',
  vus: LINEAR_VUS,
  duration: `${durationS}s`,
  startTime: `${startS}s`,
  gracefulStop: '10s',
});

export const options = {
  setupTimeout: '120s',
  insecureSkipTLSVerify: true,
  scenarios: {
    linear_warmup: linearScenario(0, WARMUP_S),
    linear_empty: linearScenario(EMPTY_AT),
    park: {
      executor: 'constant-arrival-rate',
      exec: 'park',
      rate: RATE,
      timeUnit: '1s',
      duration: `${PARK_MS}ms`,
      startTime: `${PARK_AT}s`,
      preAllocatedVUs: Math.min(MAX_VUS, Math.max(10, Math.ceil(RATE / 10))),
      maxVUs: MAX_VUS,
    },
    linear_loaded: linearScenario(LOADED_AT),
  },
  // Thresholds on the per-window submetrics make k6 keep them for handleSummary.
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'linear_starts{scenario:linear_empty}': ['count>0'],
    'linear_starts{scenario:linear_loaded}': ['count>0'],
    'linear_rejected{scenario:linear_empty}': ['count>=0'],
    'linear_rejected{scenario:linear_loaded}': ['count>=0'],
    'workflow_start_duration{scenario:linear_empty}': ['p(99)<1000'],
    'workflow_start_duration{scenario:linear_loaded}': ['p(99)<1000'],
  },
};

export function setup() {
  for (const bpmnXml of [longLivedBpmn, linear10Bpmn, warmupBpmn]) {
    const res = http.post(`${BASE_URL}/Definitions/deploy`, JSON.stringify({ BpmnXml: bpmnXml }), { headers: HEADERS });
    if (res.status !== 200) fail(`Failed to deploy a long-lived fixture: HTTP ${res.status} — ${res.body}`);
  }
  console.log(`long-lived: warm-up ${WARMUP_S}s; linear-10 (${LINEAR_VUS} VUs, ${WINDOW_S}s) from +${EMPTY_AT}s; parking ${COUNT} ` +
    `instances at ${RATE}/s from +${PARK_AT}s for ${PARK_S}s; linear-10 again from +${LOADED_AT}s`);
}

export function park() {
  const body = JSON.stringify({ WorkflowId: 'load-long-lived', Variables: { parkId: uuidv4() } });
  const res = http.post(`${BASE_URL}/Execution/start`, body, { headers: HEADERS });
  if (check(res, { 'park: status 200': (r) => r.status === 200 })) parked.add(1);
}

export function startLinear() {
  const workflowId = exec.scenario.name === 'linear_warmup' ? 'load-linear-10-warmup' : 'load-linear-10';
  const res = http.post(`${BASE_URL}/Execution/start`, JSON.stringify({ WorkflowId: workflowId }), linearParams);
  workflowStartDuration.add(res.timings.duration);
  if (res.status === 429) { linearShed.add(1); return; }
  if (check(res, { 'linear-10 start: status 200': (r) => r.status === 200 })) linearStarts.add(1);
}

export function handleSummary(data) {
  const count = (metric, scenario) => data.metrics[`${metric}{scenario:${scenario}}`]?.values.count ?? 0;
  const empty  = count('linear_starts', 'linear_empty') / WINDOW_S;
  const loaded = count('linear_starts', 'linear_loaded') / WINDOW_S;
  const drop   = empty > 0 ? (1 - loaded / empty) * 100 : NaN;
  const verdict = drop <= 10 ? 'PASS' : 'FAIL';
  const parkedCount = data.metrics.long_lived_parked?.values.count ?? 0;
  const line = `\nlong-lived: ${parkedCount} parked; linear-10 starts/s empty ${empty.toFixed(1)} → ` +
    `loaded ${loaded.toFixed(1)} (drop ${drop.toFixed(1)} %, pass ≤ 10 %): ${verdict}; 429 empty ` +
    `${count('linear_rejected', 'linear_empty')} / loaded ${count('linear_rejected', 'linear_loaded')}\n` +
    'Run scripts/long-lived.sql against the fleans database for completions and duration p99 per window.\n';
  return { stdout: textSummary(data, { indent: ' ', enableColors: true }) + line };
}
