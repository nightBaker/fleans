// tests/load/scripts/long-diverse.js
//
// Scenario 5 — long, diverse process (fixtures/long-diverse-workflow.bpmn + long-diverse-child.bpmn).
// Exercises exclusive / parallel / inclusive / event-based gateways, embedded sub-process,
// parallel + sequential multi-instance, call activity, error boundary, timer boundary,
// timer + message catch events and an external task, with four client interactions
// per instance, plus an external custom task (`e2e-probe`) served by a Plugin-role host.
// Deploy both fixtures first (setup.js deploys them).
//
// Flow per VU iteration:
//   1. POST /Execution/start  { orderId, amount, divisor }
//   2. poll until waitApproval active    → POST /Execution/message loadApproval
//   3. poll until manualReview active    → POST /Execution/complete-activity
//   4. poll until waitConfirm active     → POST /Execution/message loadConfirm
//   5. poll until isCompleted, then verify the branches taken match the inputs.
//
// Profiles (K6_PROFILE): smoke (2 VUs × 5 iterations), step (default; 10→25→50→100 VUs).
// A fixed level can be run with K6_PROFILE=constant K6_VUS=<n> K6_DURATION=<d>.

import http                    from 'k6/http';
import { check, group, sleep } from 'k6';
import { uuidv4 }              from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary }         from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import {
  workflowStartDuration,
  messageAcceptDuration,
  messageRetryAttempts,
  correlationMissRate,
  ldStageToApproval,
  ldStageToReview,
  ldStageToConfirm,
  ldStageToCompleted,
  ldEndToEnd,
  ldEngineDuration,
  ldCompletedRate,
  ldFailedRate,
  ldStallRate,
  ldPathMismatchRate,
} from './metrics.js';

const int = (name, def) => {
  const n = Number(__ENV[name]);
  return Number.isFinite(n) && n > 0 ? n : def;
};

const BASE_URL             = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const PROFILE              = __ENV.K6_PROFILE || 'step';
const POLL_INTERVAL        = int('K6_POLL_INTERVAL_MS',      250);
const POLL_CAP             = int('K6_POLL_BACKOFF_CAP_MS',  1000);
const STAGE_BUDGET         = int('K6_STAGE_BUDGET_MS',     45000);
const MESSAGE_RETRY_BUDGET = int('K6_MESSAGE_RETRY_BUDGET_MS', 5000);
const MESSAGE_RETRY_INTERVAL = int('K6_MESSAGE_RETRY_INTERVAL_MS', 200);
const ERROR_PATH_SHARE     = 0.1;
// The external `e2e-probe` step needs a Plugin-role host (docker-compose.split-kafka.yml).
const EXPECT_PLUGIN        = (__ENV.K6_EXPECT_PLUGIN || 'true') !== 'false';

const POST_HEADERS = { 'Content-Type': 'application/json' };
const GET_HEADERS  = { 'Accept': 'application/json' };

const FIXTURE = {
  processId:   'load-long-diverse',
  childId:     'load-long-child',
  approvalMsg: 'loadApproval',
  confirmMsg:  'loadConfirm',
};

const PROFILES = {
  smoke:    { executor: 'per-vu-iterations', vus: 2, iterations: 5, maxDuration: '5m' },
  step:     {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '30s', target:  10 }, { duration: '2m', target:  10 },
      { duration: '30s', target:  25 }, { duration: '2m', target:  25 },
      { duration: '30s', target:  50 }, { duration: '2m', target:  50 },
      { duration: '30s', target: 100 }, { duration: '2m', target: 100 },
      { duration: '30s', target:   0 },
    ],
    gracefulRampDown: '90s',
  },
  constant: { executor: 'constant-vus', vus: int('K6_VUS', 25), duration: __ENV.K6_DURATION || '3m' },
};

export const options = {
  scenarios: { longDiverse: { ...PROFILES[PROFILE], exec: 'longDiverseWorkflow' } },
  insecureSkipTLSVerify: true,
  thresholds: {
    http_req_failed:       ['rate<0.01'],
    ld_completed:          ['rate>0.99'],
    ld_instance_failed:    ['rate<0.01'],
    ld_path_mismatch:      ['rate<0.01'],
    ld_end_to_end:         ['p(95)<15000'],
    workflow_start_duration: ['p(95)<2000'],
  },
};

export function setup() {
  const probe = http.get(`${BASE_URL}/Definitions?page=1&pageSize=200`, { headers: GET_HEADERS });
  if (probe.status !== 200) {
    throw new Error(`[long-diverse.js] GET /Definitions returned ${probe.status} on ${BASE_URL}`);
  }
  const items = probe.json('items') || [];
  for (const key of [FIXTURE.processId, FIXTURE.childId]) {
    if (!items.some(d => d.processDefinitionKey === key && d.isActive === true)) {
      throw new Error(`[long-diverse.js] '${key}' is not deployed/active — run setup.js first.`);
    }
  }
}

// Polls the instance state until `predicate(state)` holds, the instance fails, or the
// stage budget expires. Returns { state, ok, failed }.
function pollUntil(instanceId, predicate) {
  const deadline = Date.now() + STAGE_BUDGET;
  let wait = POLL_INTERVAL;
  let state = null;
  while (Date.now() < deadline) {
    const res = http.get(`${BASE_URL}/Instances/${instanceId}/state`,
      { headers: GET_HEADERS, tags: { name: 'GET /Instances/{id}/state' } });
    if (res.status === 200) {
      state = res.json();
      if (state.isFailed) return { state, ok: false, failed: true };
      if (predicate(state)) return { state, ok: true, failed: false };
    }
    sleep(Math.min(wait, Math.max(deadline - Date.now(), 0)) / 1000);
    wait = Math.min(Math.floor(wait * 1.5), POLL_CAP);
  }
  return { state, ok: false, failed: false };
}

const isActive = (id) => (s) => (s.activeActivityIds || []).includes(id);

function sendMessage(messageName, orderId) {
  const body = JSON.stringify({ MessageName: messageName, CorrelationKey: orderId, Variables: {} });
  const deadline = Date.now() + MESSAGE_RETRY_BUDGET;
  let attempts = 0;
  let res;
  while (true) {
    res = http.post(`${BASE_URL}/Execution/message`, body,
      { headers: POST_HEADERS, tags: { name: `POST /Execution/message ${messageName}` },
        responseCallback: http.expectedStatuses(200, 404) });
    attempts += 1;
    if (res.status !== 404 || Date.now() >= deadline) break;
    sleep(MESSAGE_RETRY_INTERVAL / 1000);
  }
  messageAcceptDuration.add(res.timings.duration);
  messageRetryAttempts.add(attempts);
  correlationMissRate.add(res.status === 404);
  return res.status === 200;
}

// Runs one stage: wait for `activityId` (or completion), record its duration,
// and abort the iteration (return null) when the stage stalls or the instance fails.
function stage(instanceId, predicate, trend) {
  const t0 = Date.now();
  const r = pollUntil(instanceId, predicate);
  if (r.failed) { ldFailedRate.add(true); ldCompletedRate.add(false); return null; }
  ldStallRate.add(!r.ok);
  if (!r.ok) { ldCompletedRate.add(false); return null; }
  trend.add(Date.now() - t0);
  return r.state;
}

export function longDiverseWorkflow() {
  const orderId = uuidv4();
  const amount  = Math.floor(Math.random() * 1000) + 1;
  const divisor = Math.random() < ERROR_PATH_SHARE ? 0 : 1;
  const started = Date.now();
  let instanceId;

  group('start', () => {
    const res = http.post(`${BASE_URL}/Execution/start`,
      JSON.stringify({ WorkflowId: FIXTURE.processId, BusinessId: orderId,
                       Variables: { orderId, amount, divisor } }),
      { headers: POST_HEADERS, tags: { name: 'POST /Execution/start' } });
    workflowStartDuration.add(res.timings.duration);
    check(res, { 'start: 200': (r) => r.status === 200 });
    instanceId = res.status === 200 ? res.json('workflowInstanceId') : null;
  });
  if (!instanceId) { ldCompletedRate.add(false); return; }

  if (!stage(instanceId, isActive('waitApproval'), ldStageToApproval)) return;
  let t = Date.now();
  if (!check(sendMessage(FIXTURE.approvalMsg, orderId), { 'approval delivered': (ok) => ok })) {
    ldCompletedRate.add(false); return;
  }

  if (!stage(instanceId, isActive('manualReview'), ldStageToReview)) return;
  const cr = http.post(`${BASE_URL}/Execution/complete-activity`,
    JSON.stringify({ WorkflowInstanceId: instanceId, ActivityId: 'manualReview',
                     Variables: { reviewer: 'k6', approved: true } }),
    { headers: POST_HEADERS, tags: { name: 'POST /Execution/complete-activity' } });
  if (!check(cr, { 'complete-activity: 200': (r) => r.status === 200 })) {
    ldCompletedRate.add(false); return;
  }

  if (!stage(instanceId, isActive('waitConfirm'), ldStageToConfirm)) return;
  if (!check(sendMessage(FIXTURE.confirmMsg, orderId), { 'confirm delivered': (ok) => ok })) {
    ldCompletedRate.add(false); return;
  }

  const done = stage(instanceId, (s) => s.isCompleted, ldStageToCompleted);
  if (!done) return;
  ldFailedRate.add(false);
  ldCompletedRate.add(true);
  ldEndToEnd.add(Date.now() - started);
  if (done.createdAt && done.completedAt) {
    ldEngineDuration.add(Date.parse(done.completedAt) - Date.parse(done.createdAt));
  }

  // Branch verification — every path decision is a pure function of the start variables.
  const completed = done.completedActivityIds || [];
  const has = (id) => completed.includes(id);
  const count = (id) => completed.filter((x) => x === id).length;
  const big = amount > 500;
  const vars = Object.assign({}, ...(done.variableStates || []).map((v) => v.variables || {}));
  const probeSilo = String(vars.probeSilo || '').replace(/"/g, '');
  const pathOk =
    has(big ? 'highValue' : 'standardValue') && !has(big ? 'standardValue' : 'highValue') &&
    has('branchA2') && has('branchBSub') && has('branchCCall') &&
    (!EXPECT_PLUGIN || (has('externalWork') && probeSilo.startsWith('plugin-'))) &&
    count('subProcessItem') >= 5 && count('seqStep') >= 3 &&
    has('incAlways') && has(big ? 'incBig' : 'incSmall') && !has(big ? 'incSmall' : 'incBig') &&
    has('handleError') === (divisor === 0) &&
    has('confirmed') && !has('expired') && !has('approvalTimedOut') &&
    count('finalize') === 1 && count('end') === 1;
  ldPathMismatchRate.add(!pathOk);
  check(pathOk, { 'branches match inputs': (ok) => ok });
  if (!pathOk) {
    console.warn(`path mismatch instance=${instanceId} amount=${amount} divisor=${divisor} probeSilo=${probeSilo} completed=${JSON.stringify(completed)}`);
  }
}

export default longDiverseWorkflow;

export function handleSummary(data) {
  const out = __ENV.K6_SUMMARY_PATH;
  const res = { stdout: textSummary(data, { indent: ' ', enableColors: false }) };
  if (out) res[out] = JSON.stringify(data, null, 2);
  return res;
}
