// tests/load/scripts/events.js
//
// Scenario 3 — event-driven load test with message correlation.
// Prerequisite: run setup.js (from #239) once against the target cluster
// to bootstrap fixtures. setup() below is k6's per-run init hook — it only
// verifies deployment, not provisioning.
//
// Flow per VU iteration:
//   1. POST /Workflow/start  { WorkflowId: 'load-events', Variables: { requestId: <uuid> } }
//   2. Poll GET /Workflow/instances/{id}/state until activeActivityIds includes 'waitMessage',
//      with exponential backoff capped at K6_POLL_BACKOFF_CAP_MS and a K6_POLL_TOTAL_BUDGET_MS
//      wall-clock deadline.
//   3. POST /Workflow/message { MessageName: 'loadMessage', CorrelationKey: requestId }
//      with budgeted retry on 404 (subscription-grain commit race).

import http                        from 'k6/http';
import { check, group, sleep }     from 'k6';
import { uuidv4 }                  from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import {
  workflowStartDuration,
  pollUntilCatchDuration,
  messageAcceptDuration,
  messageRetryAttempts,
  pollStallsRate,
  correlationMissRate,
} from './metrics.js';
import thresholds from '../thresholds.json';

const int = (name, def) => {
  const n = Number(__ENV[name]);
  return Number.isFinite(n) && n > 0 ? n : def;
};

const BASE_URL             = __ENV.K6_TARGET_URL || 'https://localhost:7140';
const POLL_INTERVAL        = int('K6_POLL_INTERVAL_MS',        100);
const POLL_MAX             = int('K6_POLL_MAX_ATTEMPTS',        20);
const POLL_CAP             = int('K6_POLL_BACKOFF_CAP_MS',     500);
const POLL_TOTAL_BUDGET    = int('K6_POLL_TOTAL_BUDGET_MS',   3000);
const MESSAGE_RETRY_BUDGET   = int('K6_MESSAGE_RETRY_BUDGET_MS',   1000);
const MESSAGE_RETRY_INTERVAL = int('K6_MESSAGE_RETRY_INTERVAL_MS',  150);

const POST_HEADERS = { 'Content-Type': 'application/json' };
const GET_HEADERS  = { 'Accept': 'application/json' };

// Must match tests/load/fixtures/events-workflow.bpmn verbatim — see #238 / PR #323.
// On fixture rename, update this block only.
const FIXTURE = {
  processId:      'load-events',
  catchId:        'waitMessage',
  messageName:    'loadMessage',
  correlationVar: 'requestId',
};

export const options = {
  stages: [
    { duration: '1m', target: 200 },
    { duration: '3m', target: 200 },
    { duration: '1m', target:   0 },
  ],
  insecureSkipTLSVerify: true,
  thresholds: { ...thresholds },
};

export function setup() {
  const probe = http.get(
    `${BASE_URL}/Workflow/definitions?page=1&pageSize=200`,
    { headers: GET_HEADERS });

  if (probe.status !== 200) {
    throw new Error(
      `[events.js] Setup probe GET /Workflow/definitions returned ${probe.status}. ` +
      `Cannot start a load run against ${BASE_URL}.`);
  }

  const items = probe.json('items') || [];
  const deployed = items.some(d =>
    d.processDefinitionKey === FIXTURE.processId && d.isActive === true);

  if (!deployed) {
    throw new Error(
      `[events.js] Process '${FIXTURE.processId}' is not deployed (or not active) on ${BASE_URL}.\n` +
      `  1. Open the Workflows page: ${BASE_URL}/workflows\n` +
      `  2. Click "Deploy BPMN" and select: tests/load/fixtures/events-workflow.bpmn\n` +
      `  3. Re-run this script.\n` +
      `See tests/load/README.md for the full load-test walk-through.`);
  }
}

export function eventsWorkflow() {
  const requestId = uuidv4();
  let instanceId;

  // Phase 1 — start
  group('start', () => {
    const res = http.post(
      `${BASE_URL}/Workflow/start`,
      JSON.stringify({ WorkflowId: FIXTURE.processId, Variables: { [FIXTURE.correlationVar]: requestId } }),
      { headers: POST_HEADERS });
    workflowStartDuration.add(res.timings.duration);
    check(res, { 'start: status 200': (r) => r.status === 200 });
    instanceId = res.json('workflowInstanceId');
  });
  if (!instanceId) return;

  // Phase 2 — poll for waitMessage with exponential backoff and wall-clock budget.
  // `return` inside the group callback exits only the callback; the outer function
  // reads `caught` to decide whether to continue to the message phase.
  let caught = false;
  group('poll', () => {
    const pollStart    = Date.now();
    const pollDeadline = pollStart + POLL_TOTAL_BUDGET;
    let wait = POLL_INTERVAL;
    for (let i = 0; i < POLL_MAX; i++) {
      if (Date.now() >= pollDeadline) break;
      const res = http.get(
        `${BASE_URL}/Workflow/instances/${instanceId}/state`,
        { headers: GET_HEADERS });
      if (res.status === 200) {
        const activeIds = res.json('activeActivityIds') || [];
        if (activeIds.includes(FIXTURE.catchId)) {
          pollUntilCatchDuration.add(Date.now() - pollStart);
          caught = true;
          return;
        }
      }
      const remaining = pollDeadline - Date.now();
      if (remaining <= 0) break;
      if (i < POLL_MAX - 1) sleep(Math.min(wait, remaining) / 1000);
      wait = Math.min(Math.floor(wait * 1.5), POLL_CAP);
    }
  });
  // poll_stalls: p(95)<2500 threshold keeps slow-but-caught iterations separate
  // from hard timeouts — don't lower K6_POLL_TOTAL_BUDGET_MS below the threshold
  // without adjusting thresholds.json.
  pollStallsRate.add(!caught);
  check(caught, { 'caught waitMessage': (c) => c === true });
  if (!caught) return;

  // Phase 3 — send message with budgeted retry on 404 (subscription-grain commit race).
  // GET /instances/{id}/state reads the EF projection; POST /Workflow/message dispatches
  // via IMessageCorrelationGrain.DeliverMessage (direct grain call). These are independent
  // commit paths — seeing waitMessage in activeActivityIds is evidence the projection saw
  // the event, not proof the subscription grain has committed.
  group('message', () => {
    const body = JSON.stringify({
      MessageName:    FIXTURE.messageName,
      CorrelationKey: requestId,
      Variables:      {},
    });

    const deadline = Date.now() + MESSAGE_RETRY_BUDGET;
    let attempts = 0;
    let res;
    while (true) {
      res = http.post(`${BASE_URL}/Workflow/message`, body, { headers: POST_HEADERS });
      attempts += 1;
      if (res.status !== 404) break;
      if (Date.now() >= deadline) break;
      sleep(MESSAGE_RETRY_INTERVAL / 1000);
    }

    messageAcceptDuration.add(res.timings.duration);
    messageRetryAttempts.add(attempts);
    correlationMissRate.add(res.status === 404);

    check(res, { 'message: status 200': (r) => r.status === 200 });
    check(attempts, { 'message: first-try success': (a) => a === 1 });
  });
}

export default eventsWorkflow;
