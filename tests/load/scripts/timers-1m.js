// tests/load/scripts/timers-1m.js
//
// Scenario timers-1m (#886): arm K6_TIMERS_COUNT durable BPMN timers (default 1 000 000), all
// due within ONE minute, then measure how late they fire. Answers whether Orleans reminders on
// PostgreSQL hold a million pending timers, or whether the sharded timer scheduler (roadmap
// "Timers", step 2) is needed. Pass: timer lag p99 < 2 s, no timer lost.
//
// How it works:
//   1. setup() picks T0 = now + arming time + K6_TIMERS_SLACK_S and deploys 60 copies of
//      fixtures/timers-workflow.bpmn — load-timers-00 … load-timers-59 — whose timeDate is
//      T0 + 0 s … T0 + 59 s.
//   2. The `arm` scenario starts instances round-robin over the 60 keys at K6_TIMERS_RATE/s.
//      Each instance parks on its timer (one TimerCallbackGrain + one Orleans reminder).
//   3. k6 exits after arming. The timers fire during [T0, T0 + 60 s]; afterwards run
//      scripts/timers-1m-lag.sql against the `fleans` database for armed / fired counts and
//      lag p50 / p99 / max (CompletedAt − due time, from the read model).
//
// TARGET INFRASTRUCTURE: the tests/load docker-compose stack (reminders on their own Postgres)
// or a cloud cluster. Use a FRESH stack per run (`docker compose down -v`): the lag query reads
// every load-timers-* instance in the database. See tests/load/README.md § timers-1m.
//
//   K6_TARGET_URL=http://localhost:80 k6 run tests/load/scripts/timers-1m.js
//   K6_TARGET_URL=http://localhost:80 K6_TIMERS_COUNT=10000 K6_TIMERS_RATE=200 k6 run tests/load/scripts/timers-1m.js   # smoke

import http      from 'k6/http';
import exec      from 'k6/execution';
import { check, fail } from 'k6';
import { Counter, Rate } from 'k6/metrics';
import { workflowStartDuration } from './metrics.js';

const template = open('../fixtures/timers-workflow.bpmn');

const BASE_URL = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const HEADERS  = { 'Content-Type': 'application/json' };
const KEYS     = 60; // one key per second of the due minute

function positiveInt(name, fallback) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return fallback;
  const n = Number(raw);
  if (!Number.isInteger(n) || n <= 0) throw new Error(`${name} must be a positive integer (got '${raw}')`);
  return n;
}

const COUNT   = positiveInt('K6_TIMERS_COUNT', 1000000);
const RATE    = positiveInt('K6_TIMERS_RATE', 2000);       // instance starts per second
const SLACK_S = positiveInt('K6_TIMERS_SLACK_S', 120);     // margin between arming end and T0
const MAX_VUS = positiveInt('K6_TIMERS_MAX_VUS', 1000);
const ARM_S   = Math.ceil(COUNT / RATE);

const timersArmed     = new Counter('timers_armed');
const timersArmedLate = new Rate('timers_armed_late');    // started after its timer was already due

export const options = {
  setupTimeout: '120s',
  insecureSkipTLSVerify: true,
  scenarios: {
    arm: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: `${ARM_S}s`,
      preAllocatedVUs: Math.min(MAX_VUS, Math.max(10, Math.ceil(RATE / 10))),
      maxVUs: MAX_VUS,
    },
  },
  thresholds: {
    http_req_failed:   ['rate<0.01'],
    timers_armed_late: ['rate<0.001'],
  },
};

const pad = (n) => String(n).padStart(2, '0');

export function setup() {
  const t0 = Date.now() + (ARM_S + SLACK_S) * 1000;
  for (let i = 0; i < KEYS; i++) {
    const dueAt = new Date(t0 + i * 1000).toISOString();
    const bpmnXml = template
      .replace('__PROCESS_ID__', `load-timers-${pad(i)}`)
      .replace('__DUE_AT__', dueAt);
    const res = http.post(`${BASE_URL}/Definitions/deploy`, JSON.stringify({ BpmnXml: bpmnXml }), { headers: HEADERS });
    if (res.status !== 200) fail(`Failed to deploy load-timers-${pad(i)}: HTTP ${res.status} — ${res.body}`);
  }
  console.log(`timers-1m: arming ${COUNT} timers at ${RATE}/s for ${ARM_S}s; due ${new Date(t0).toISOString()} … +59 s`);
  return { t0 };
}

export default function (data) {
  const key = `load-timers-${pad(exec.scenario.iterationInTest % KEYS)}`;
  const res = http.post(`${BASE_URL}/Execution/start`, JSON.stringify({ WorkflowId: key }), { headers: HEADERS });

  workflowStartDuration.add(res.timings.duration);
  const ok = check(res, { 'timer instance start: status 200': (r) => r.status === 200 });
  if (ok) timersArmed.add(1);
  timersArmedLate.add(Date.now() >= data.t0 + (exec.scenario.iterationInTest % KEYS) * 1000);
}

export function teardown(data) {
  const doneBy = new Date(data.t0 + 60 * 1000).toISOString();
  console.log(`timers-1m: timers fall due until ${doneBy}. Wait ~1 minute past it, then run ` +
    `scripts/timers-1m-lag.sql against the fleans database (see tests/load/README.md § timers-1m).`);
}
