// linear-saturation.js — saturation search script for Phase 3B/3C
// Extends linear.js stages to find the VU count where error rate > 1% or p95 > 2s.
// Do NOT modify linear.js for this purpose — this is a separate script so Phase 3A
// results remain valid for comparison.

import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE_URL = __ENV.K6_TARGET_URL || 'http://localhost:80';

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
  thresholds: {
    // Record saturation: the first stage where either threshold is exceeded.
    'http_req_failed': ['rate<0.01'],   // error rate < 1%
    'http_req_duration': ['p(95)<2000'], // p95 < 2s
  },
};

export default function () {
  const res = http.get(`${BASE_URL}/health`);
  check(res, { 'status 200': (r) => r.status === 200 });
  sleep(0.1);
}
