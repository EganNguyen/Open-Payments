import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

const rate = Number(__ENV.RATE || 25000);
const rampSeconds = Number(__ENV.RAMP || 30);
const holdSeconds = Number(__ENV.HOLD || 30);

const addedLatency = new Trend('mock_added_latency_ms', true);
const completed = new Rate('mock_completed');
const status200 = new Counter('status_200');
const status429 = new Counter('status_429');
const status500 = new Counter('status_500');
const statusOther = new Counter('status_other');

export const options = {
  scenarios: {
    capacity: {
      executor: 'ramping-arrival-rate',
      startRate: Math.max(1, Math.min(1000, rate)),
      timeUnit: '1s',
      preAllocatedVUs: Math.max(100, Math.ceil(rate * 0.25)),
      maxVUs: Math.max(1000, Math.ceil(rate * 0.5)),
      stages: [
        { target: rate, duration: `${rampSeconds}s` },
        { target: rate, duration: `${holdSeconds}s` },
      ],
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
  completed.add(response.status === 200);

  if (response.status === 200) {
    status200.add(1);
  } else if (response.status === 429) {
    status429.add(1);
  } else if (response.status === 500) {
    status500.add(1);
  } else {
    statusOther.add(1);
  }

  check(response, { 'status is 200': (result) => result.status === 200 });
}
