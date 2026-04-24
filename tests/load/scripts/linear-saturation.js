// linear-saturation.js — saturation search script for Phase 3B/3C
// Extends linear.js stages to find the VU count where error rate > 1% or p95 > 2s.
// Uses the same POST /Workflow/start payload as linear.js so results measure
// actual workflow-execution saturation, not HTTP-pipeline saturation.
//
// Prerequisites:
//   1. Target cluster running
//   2. Fixtures deployed: k6 run --insecure-skip-tls-verify tests/load/scripts/setup.js

import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.K6_TARGET_URL || 'http://localhost:80';
const HEADERS = { 'Content-Type': 'application/json' };

export const options = {
  stages: [
    { duration: '2m', target: 50 },
    { duration: '2m', target: 100 },
    { duration: '2m', target: 200 },
    { duration: '2m', target: 500 },
    { duration: '2m', target: 1000 },
    { duration: '2m', target: 2000 },
    { duration: '2m', target: 0 },
  ],
  insecureSkipTLSVerify: true,
  thresholds: {
    // Record saturation: the first stage where either threshold is exceeded.
    'http_req_failed': ['rate<0.01'],   // error rate < 1%
    'http_req_duration': ['p(95)<2000'], // p95 < 2s
  },
};

export default function () {
  const payload = JSON.stringify({ WorkflowId: 'load-linear' });
  const res = http.post(`${BASE_URL}/Workflow/start`, payload, { headers: HEADERS });
  check(res, { 'workflow start: status 200': (r) => r.status === 200 });
  sleep(0.1);
}
