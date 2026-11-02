// tests/load/scripts/signal-fanout.js
//
// Scenario signal-fanout (#976, roadmap "How it is measured"): park K6_FANOUT_COUNT instances on one
// signal catch (default 100 000), broadcast the signal once, and check that every instance receives
// it while linear-10 starts keep their rate and latency. Pass: no instance left waiting; linear-10
// start p99 during the broadcast within 10 % of the one before it, and no failed requests.
//
// Timeline (all scenarios in one run):
//   park           constant-arrival-rate starts of load-signal-fanout at K6_FANOUT_RATE/s
//   linear_before  linear-10 starts at K6_LINEAR_RATE/s for K6_WINDOW_S, after a K6_SETTLE_S pause
//   broadcast      one POST /Execution/signal, when linear_before ends
//   linear_during  linear-10 starts at the same rate for K6_WINDOW_S, from the broadcast on
// Then run scripts/signal-fanout.sql against the `fleans` database for parked / received / waiting
// and the delivery spread. Use a FRESH stack per run (`docker compose down -v`): the query reads
// every load-signal-fanout instance in the database.
//
// Start the stack with overrides/signal-fanout.yml: the read-model projector on the default 512 MB
// worker runs out of memory from ~10 000 parked instances (see tests/load/README.md § signal-fanout).
//
//   K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/signal-fanout.js
//   K6_TARGET_URL=http://localhost:80 K6_FANOUT_COUNT=20000 K6_FANOUT_RATE=150 k6 run tests/load/scripts/signal-fanout.js   # laptop

import http      from 'k6/http';
import { check, fail } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { workflowStartDuration } from './metrics.js';

const fanoutBpmn   = open('../fixtures/signal-fanout-workflow.bpmn');
const linear10Bpmn = open('../fixtures/linear-10-workflow.bpmn');

const BASE_URL    = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS     = { 'Content-Type': 'application/json' };
const SIGNAL_NAME = 'loadFanout'; // must match fixtures/signal-fanout-workflow.bpmn

function positiveInt(name, fallback) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return fallback;
  const n = Number(raw);
  if (!Number.isInteger(n) || n <= 0) throw new Error(`${name} must be a positive integer (got '${raw}')`);
  return n;
}

const COUNT       = positiveInt('K6_FANOUT_COUNT', 100000);
const RATE        = positiveInt('K6_FANOUT_RATE', 1000);   // parked starts per second
const LINEAR_RATE = positiveInt('K6_LINEAR_RATE', 20);     // linear-10 starts per second
const WINDOW_S    = positiveInt('K6_WINDOW_S', 60);        // length of each linear-10 window
const SETTLE_S    = positiveInt('K6_SETTLE_S', 30);        // pause after parking, for the last subscriptions
const MAX_VUS     = positiveInt('K6_FANOUT_MAX_VUS', 1000);
const PARK_S      = Math.ceil(COUNT / RATE);
const BEFORE_AT   = PARK_S + SETTLE_S;
const DURING_AT   = BEFORE_AT + WINDOW_S;

const fanoutParked      = new Counter('fanout_parked');
const fanoutDelivered   = new Counter('fanout_delivered');      // DeliveredCount of the broadcast
const broadcastDuration = new Trend('fanout_broadcast_duration', true);

const linearScenario = (startS) => ({
  executor: 'constant-arrival-rate',
  exec: 'startLinear',
  rate: LINEAR_RATE,
  timeUnit: '1s',
  duration: `${WINDOW_S}s`,
  startTime: `${startS}s`,
  preAllocatedVUs: Math.max(5, LINEAR_RATE),
  maxVUs: Math.max(50, LINEAR_RATE * 10),
});

export const options = {
  setupTimeout: '120s',
  insecureSkipTLSVerify: true,
  scenarios: {
    park: {
      executor: 'constant-arrival-rate',
      exec: 'park',
      rate: RATE,
      timeUnit: '1s',
      duration: `${PARK_S}s`,
      preAllocatedVUs: Math.min(MAX_VUS, Math.max(10, Math.ceil(RATE / 10))),
      maxVUs: MAX_VUS,
    },
    linear_before: linearScenario(BEFORE_AT),
    broadcast: {
      executor: 'shared-iterations',
      exec: 'broadcast',
      vus: 1,
      iterations: 1,
      startTime: `${DURING_AT}s`,
      maxDuration: '10m',
    },
    linear_during: linearScenario(DURING_AT),
  },
  // The two linear-10 windows show up side by side in the summary; compare their p99.
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'workflow_start_duration{scenario:linear_before}': ['p(99)<1000'],
    'workflow_start_duration{scenario:linear_during}': ['p(99)<1000'],
    fanout_broadcast_duration: ['max<60000'],
  },
};

export function setup() {
  for (const bpmnXml of [fanoutBpmn, linear10Bpmn]) {
    const res = http.post(`${BASE_URL}/Definitions/deploy`, JSON.stringify({ BpmnXml: bpmnXml }), { headers: HEADERS });
    if (res.status !== 200) fail(`Failed to deploy a signal-fanout fixture: HTTP ${res.status} — ${res.body}`);
  }
  console.log(`signal-fanout: parking ${COUNT} instances at ${RATE}/s for ${PARK_S}s; ` +
    `linear-10 at ${LINEAR_RATE}/s from +${BEFORE_AT}s; broadcast at +${DURING_AT}s`);
}

export function park() {
  const res = http.post(`${BASE_URL}/Execution/start`, JSON.stringify({ WorkflowId: 'load-signal-fanout' }), { headers: HEADERS });
  if (check(res, { 'park: status 200': (r) => r.status === 200 })) fanoutParked.add(1);
}

export function startLinear() {
  const res = http.post(`${BASE_URL}/Execution/start`, JSON.stringify({ WorkflowId: 'load-linear-10' }), { headers: HEADERS });
  workflowStartDuration.add(res.timings.duration);
  check(res, { 'linear-10 start: status 200': (r) => r.status === 200 });
}

export function broadcast() {
  const sentAt = new Date().toISOString();
  const res = http.post(`${BASE_URL}/Execution/signal`, JSON.stringify({ SignalName: SIGNAL_NAME }),
    { headers: HEADERS, timeout: '10m' });
  broadcastDuration.add(res.timings.duration);
  check(res, { 'broadcast: status 200': (r) => r.status === 200 });
  const delivered = res.status === 200 ? res.json('deliveredCount') ?? res.json('DeliveredCount') : 0;
  fanoutDelivered.add(delivered || 0);
  console.log(`signal-fanout: broadcast sent at ${sentAt}, answered in ${Math.round(res.timings.duration)} ms, ` +
    `DeliveredCount=${delivered}, body=${String(res.body).slice(0, 200)}`);
}

export function teardown() {
  console.log('signal-fanout: run scripts/signal-fanout.sql against the fleans database once the ' +
    'projector has caught up (see tests/load/README.md § signal-fanout).');
}
