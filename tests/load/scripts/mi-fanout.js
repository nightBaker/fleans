// tests/load/scripts/mi-fanout.js
//
// Scenario mi-fanout (#861): one instance forks N parallel multi-instance branches, each waiting for
// its own message. Every subscription persists before its external call, all in the start's turn, so
// the start's answer time shows whether that turn reaches the 30 s grain-call timeout. One instance
// per size, one at a time: this measures a single turn, not throughput.
//
// Pass: every start answers 200 within 30 s with every branch waiting, and the instance completes
// once every message is delivered.
//
//   K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/mi-fanout.js
//   K6_TARGET_URL=http://localhost:80 K6_MI_SIZES=1000,5000 k6 run tests/load/scripts/mi-fanout.js
//
// It prints the instance ids; scripts/mi-fanout.sql gives their journal and snapshot sizes.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const bpmn = open('../fixtures/mi-fanout-workflow.bpmn');

const BASE_URL = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS = { 'Content-Type': 'application/json' };
const PROCESS_ID = 'load-mi-fanout';
const MESSAGE_NAME = 'loadMiFanout'; // must match fixtures/mi-fanout-workflow.bpmn
const CATCH_ID = 'waitItem';
const SIZES = (__ENV.K6_MI_SIZES || '100,500,1000,2000').split(',').map(Number);
const DELIVERY_CONCURRENCY = Number(__ENV.K6_MI_DELIVERY_CONCURRENCY || 50);
const COMPLETE_TIMEOUT_S = Number(__ENV.K6_MI_COMPLETE_TIMEOUT_S || 600);

const startDuration = new Trend('mi_fanout_start_duration', true);

export const options = {
  insecureSkipTLSVerify: true,
  scenarios: {
    fanout: {
      executor: 'shared-iterations',
      vus: 1,
      iterations: SIZES.length,
      maxDuration: '60m',
    },
  },
  thresholds: {
    mi_fanout_start_duration: ['max<30000'],
    checks: ['rate==1'],
  },
};

export function setup() {
  const res = http.post(`${BASE_URL}/Definitions/deploy`, JSON.stringify({ BpmnXml: bpmn }), { headers: HEADERS });
  if (res.status !== 200) fail(`Failed to deploy the mi-fanout fixture: HTTP ${res.status} — ${res.body}`);
}

function state(instanceId) {
  const res = http.get(`${BASE_URL}/Instances/${instanceId}/state`, { timeout: '60s', responseType: 'text' });
  return res.status === 200 ? res.json() : null;
}

/** Polls until `done(state)` holds; returns the seconds it took, or null on timeout. */
function waitFor(instanceId, timeoutS, done) {
  const startedAt = Date.now();
  while ((Date.now() - startedAt) / 1000 < timeoutS) {
    const s = state(instanceId);
    if (s && done(s)) return (Date.now() - startedAt) / 1000;
    sleep(0.5);
  }
  return null;
}

function deliver(keys) {
  for (let i = 0; i < keys.length; i += DELIVERY_CONCURRENCY) {
    const responses = http.batch(keys.slice(i, i + DELIVERY_CONCURRENCY).map((key) => ['POST',
      `${BASE_URL}/Execution/message`,
      JSON.stringify({ MessageName: MESSAGE_NAME, CorrelationKey: key, Variables: {} }),
      { headers: HEADERS, timeout: '120s' }]));
    check(responses, { 'every message delivered': (rs) => rs.every((r) => r.status === 200) });
  }
}

export default function () {
  const size = SIZES[__ITER];
  const runId = `${Date.now()}-${size}`;
  const items = Array.from({ length: size }, (_, i) => `${runId}-${i}`);

  const res = http.post(`${BASE_URL}/Execution/start`,
    JSON.stringify({ WorkflowId: PROCESS_ID, Variables: { items } }),
    { headers: HEADERS, timeout: '150s', responseType: 'text' });
  startDuration.add(res.timings.duration, { size: String(size) });
  const started = check(res, { 'start answered 200': (r) => r.status === 200 });
  if (!started) {
    console.log(`size=${size} start HTTP ${res.status} after ${res.timings.duration.toFixed(0)} ms: ${String(res.body).slice(0, 200)}`);
    return;
  }

  // A 200 means the start's turn, and so every subscription, is done.
  const instanceId = res.json().workflowInstanceId;
  const branches = (state(instanceId)?.activeActivities || []).filter((a) => a.activityId === CATCH_ID).length;
  check(branches, { 'every branch waiting': (n) => n === size });

  const deliveredAt = Date.now();
  deliver(items);
  const deliveredS = (Date.now() - deliveredAt) / 1000;
  const completedS = waitFor(instanceId, COMPLETE_TIMEOUT_S, (s) => s.isCompleted === true);
  check(completedS, { 'instance completed': (v) => v !== null });

  console.log(`size=${size} id=${instanceId} start_ms=${res.timings.duration.toFixed(0)} ` +
    `delivered_s=${deliveredS.toFixed(1)} ` +
    `completed_after_delivery_s=${completedS === null ? 'timeout' : completedS.toFixed(1)}`);
}
