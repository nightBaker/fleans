// throughput.js — fixed start rate of a no-wait process, for the roadmap's headline metric:
// completed instances per second and p99 process duration (website roadmap/v1 "How it is measured").
//
// The script only starts instances, at a constant arrival rate (open model: a slow cluster does not
// slow the offered load down). It does not poll: completions and durations come from the read
// model afterwards (throughput-window.sql — CompletedAt − CreatedAt, both from the journal events'
// OccurredAt), so the client adds no load and no polling error to the measurement.
// throughput-ceiling.sh runs it at several rates and prints one row per rate.
//
// Knobs are TP_* rather than K6_*: k6 reads K6_DURATION / K6_VUS itself and would replace the scenarios.
//   TP_PROCESS   load-linear-10 (default; roadmap linear-10) | load-one-task (Zeebe's reference process)
//   TP_RATE      starts per second (default 100)
//   TP_DURATION  measured window, e.g. 5m (default 2m); TP_WARMUP is run first at the same rate (default 30s)
//   TP_PAYLOAD_KB  size of one string variable per instance (default 0; Zeebe's benchmark uses ~32 KB)
//   TP_MAX_VUS   cap on concurrent requests (default 2000)
//
//   K6_TARGET_URL=http://localhost:80 TP_RATE=200 k6 run tests/load/scripts/throughput.js

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { workflowStartDuration } from './metrics.js';

const BASE_URL = __ENV.K6_TARGET_URL || 'http://localhost:80';
const PROCESS  = __ENV.TP_PROCESS || 'load-linear-10';
const RATE     = parseInt(__ENV.TP_RATE || '100', 10);
const DURATION = __ENV.TP_DURATION || '2m';
const WARMUP   = __ENV.TP_WARMUP || '30s';
const MAX_VUS  = parseInt(__ENV.TP_MAX_VUS || '2000', 10);
const PAYLOAD  = 'x'.repeat(parseInt(__ENV.TP_PAYLOAD_KB || '0', 10) * 1024);
const HEADERS  = { 'Content-Type': 'application/json' };

const startsRejected = new Counter('starts_rejected');

const scenario = (startTime, duration) => ({
  executor: 'constant-arrival-rate',
  rate: RATE,
  timeUnit: '1s',
  duration,
  startTime,
  preAllocatedVUs: Math.min(MAX_VUS, Math.max(50, RATE)),
  maxVUs: MAX_VUS,
});

export const options = {
  scenarios: {
    warmup:   { ...scenario('0s', WARMUP), tags: { phase: 'warmup' } },
    measured: { ...scenario(WARMUP, DURATION), tags: { phase: 'measured' } },
  },
  insecureSkipTLSVerify: true,
  // k6 only checks the client side; the pass criteria (completed/s, p99, errors < 1 %) are in throughput-window.sql.
  thresholds: {
    'http_req_failed{phase:measured}': ['rate<0.01'],
    'dropped_iterations': ['count<1'],
  },
};

export default function () {
  const variables = PAYLOAD ? { payload: PAYLOAD } : {};
  const res = http.post(`${BASE_URL}/Execution/start`,
    JSON.stringify({ WorkflowId: PROCESS, Variables: variables }),
    { headers: HEADERS, tags: { name: 'POST /Execution/start' } });
  workflowStartDuration.add(res.timings.duration);
  if (!check(res, { 'start: 200': (r) => r.status === 200 })) startsRejected.add(1);
}
