// Load test for the redirect path, GET /{code}.
//
//   k6 run loadtest/redirect.js
//   k6 run -e RATE=500 -e DURATION=60s -e HOT_SHARE=0.8 loadtest/redirect.js
//
// Settings (all optional, passed with -e):
//   BASE_URL   the app, default http://localhost:5154
//   LINKS      links created in setup, default 1000
//   RATE       redirects per second, held constant, default 100
//   DURATION   how long to hold that rate, default 30s
//   HOT_SHARE  share of requests that go to the hottest 1% of codes, default 0 (uniform)
//   P99_MS     the p99 latency the run must stay under to pass, default 200
//
// The app must allow LINKS posts at once: raise RateLimit__Capacity and RateLimit__TokensPerPeriod.
import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5154';
const LINKS = Number(__ENV.LINKS || 1000);
const RATE = Number(__ENV.RATE || 100);
const DURATION = __ENV.DURATION || '30s';
const HOT_SHARE = Number(__ENV.HOT_SHARE || 0);
const P99_MS = Number(__ENV.P99_MS || 200);

export const options = {
  // Count the 302 itself; following it would also load the target site.
  maxRedirects: 0,
  setupTimeout: '5m',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    redirects: {
      // Starts RATE iterations a second whatever the response times, so a slow app shows up as
      // higher latency, not as a lower request rate.
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 50,
      maxVUs: 1000,
    },
  },
  // Filtered by scenario, so the setup's POSTs don't count. The summary prints these sub-metrics.
  thresholds: {
    'http_req_failed{scenario:redirects}': ['rate<0.01'],
    'http_req_duration{scenario:redirects}': [`p(99)<${P99_MS}`],
  },
};

// Runs once before the load: creates the links and hands their codes to every virtual user.
export function setup() {
  const runId = Date.now();
  const codes = [];
  for (let start = 0; start < LINKS; start += 100) {
    const batch = [];
    for (let i = start; i < Math.min(start + 100, LINKS); i++) {
      batch.push({
        method: 'POST',
        url: `${BASE_URL}/shorten`,
        body: JSON.stringify({ url: `https://example.com/load/${runId}/${i}` }),
        params: { headers: { 'Content-Type': 'application/json' } },
      });
    }
    for (const res of http.batch(batch)) {
      if (res.status !== 201) {
        throw new Error(`POST /shorten returned ${res.status}; is the rate limit raised?`);
      }
      codes.push(res.json('code'));
    }
  }
  return { codes };
}

// One iteration = one redirect.
export default function ({ codes }) {
  const hot = Math.max(1, Math.floor(codes.length / 100));
  const pool = Math.random() < HOT_SHARE ? hot : codes.length;
  const code = codes[Math.floor(Math.random() * pool)];
  const res = http.get(`${BASE_URL}/${code}`, { tags: { name: 'GET /{code}' } });
  check(res, { 'status is 302': (r) => r.status === 302 });
}
