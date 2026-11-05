import http from 'k6/http';
import { Trend } from 'k6/metrics';

const rate = Number(__ENV.RATE || 25000);
const holdSeconds = Number(__ENV.HOLD || 30);
const shards = Number(__ENV.SHARDS || 3);

const addedLatency = new Trend('mock_added_latency_ms', true);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    capacity: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration: `${holdSeconds}s`,
      preAllocatedVUs: Math.ceil(rate * 0.25),
      maxVUs: Math.ceil(rate * 0.6),
      gracefulStop: '30s',
    },
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max', 'count'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
  },
};

function shardFor(value) {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index++) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return Math.abs(hash) % shards;
}

export default function () {
  const key = `${__VU}-${__ITER}-${Date.now()}-${Math.random()}`;
  const account = `bench-${__VU % 100}`;
  const port = 18080 + shardFor(key);
  const response = http.post(
    `http://127.0.0.1:${port}/authorize`,
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
