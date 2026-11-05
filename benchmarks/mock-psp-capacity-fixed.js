import http from 'k6/http';
import { Trend } from 'k6/metrics';

const rate = Number(__ENV.RATE || 25000);
const holdSeconds = Number(__ENV.HOLD || 30);
const vus = Number(__ENV.VUS || Math.ceil(rate * 0.3));

const addedLatency = new Trend('mock_added_latency_ms', true);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    capacity: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration: `${holdSeconds}s`,
      preAllocatedVUs: vus,
      maxVUs: vus,
      gracefulStop: '30s',
    },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max', 'count'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
  },
};

export default function () {
  const key = `${__VU}-${__ITER}-${Date.now()}-${Math.random()}`;
  const account = `bench-${__VU % 100}`;
  const response = http.post(
    'http://127.0.0.1:18080/authorize',
    JSON.stringify({ amount_minor: 100, currency: 'USD' }),
    {
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': key,
        'X-Mock-Account': account,
      },
    },
  );
  addedLatency.add(response.timings.duration - 200);
}
