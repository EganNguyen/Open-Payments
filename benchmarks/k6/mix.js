import http from "k6/http";
import { check } from "k6";
import { Counter, Rate } from "k6/metrics";
import { SharedArray } from "k6/data";
import { pickMerchant, pickRecent, remember, uuidv4 } from "./lib/keys.js";

const steps = JSON.parse(open("../profiles/steps.json"));
const stepNumber = Number(__ENV.STEP || 0);
const step = steps.find((candidate) => candidate.step === stepNumber) || steps[0];
const rate = Number(__ENV.RATE || step.rate);
const durationSeconds = Number(__ENV.DURATION || step.duration_seconds);
const rampSeconds = Number(__ENV.RAMP_SECONDS || 5);
const targetOverride = (__ENV.TARGET_OVERRIDE || "api").toLowerCase();
const mock = targetOverride === "mock";
const baseUrl = __ENV.BASE_URL || (mock ? "http://127.0.0.1:18080" : "http://127.0.0.1:8080");
const apiToken = __ENV.API_TOKEN || "";
const autoCaptureShare = Number(__ENV.AUTO_CAPTURE_SHARE || 0.5);
const refundTargetsPath = __ENV.REFUND_TARGETS || "";

const refundTargets = refundTargetsPath
  ? new SharedArray("refund targets", () => JSON.parse(open(refundTargetsPath)))
  : [];

const admitted = new Counter("k6_requests_admitted_total");
const rejected = new Counter("k6_requests_rejected_total");
const completed = new Counter("k6_payments_completed_total");
const unexpected = new Rate("op_unexpected_error");

const recent = [];
const recentLimit = 128;

export const options = {
  discardResponseBodies: false,
  scenarios: {
    mix: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      preAllocatedVUs: Math.max(10, Math.ceil(rate * 0.3)),
      maxVUs: Math.max(100, Math.ceil(rate * 0.8)),
      stages: [
        { target: rate, duration: `${rampSeconds}s` },
        { target: rate, duration: `${durationSeconds}s` },
      ],
      gracefulStop: "30s",
    },
  },
  thresholds: {
    op_unexpected_error: ["rate<0.001"],
  },
  summaryTrendStats: ["avg", "p(50)", "p(95)", "p(99)", "max"],
};

export default function () {
  admitted.add(1);
  const roll = Math.random();
  if (roll < 0.6) {
    payment();
  } else if (roll < 0.75) {
    status();
  } else if (roll < 0.85) {
    refund();
  } else if (roll < 0.95) {
    retry();
  } else {
    other();
  }
}

function headers(merchant, key, json) {
  const result = {};
  if (json) {
    result["Content-Type"] = "application/json";
  }
  if (key) {
    result["Idempotency-Key"] = key;
  }
  if (mock) {
    result["X-Mock-Account"] = merchant;
  } else if (apiToken) {
    result["Authorization"] = `Bearer ${apiToken}`;
  }
  return result;
}

function bodyFor(amount, captureMode) {
  if (mock) {
    return { amount_minor: amount, currency: "USD" };
  }
  return {
    amount_minor: amount,
    currency: "USD",
    method: { type: "card", ref: `tok_${uuidv4()}` },
    capture_mode: captureMode,
  };
}

function payment() {
  const merchant = pickMerchant();
  const key = uuidv4();
  const amount = 100 + Math.floor(Math.random() * 9900);
  const captureMode = Math.random() < autoCaptureShare ? "auto" : "manual";
  const body = bodyFor(amount, captureMode);
  const path = mock ? "/authorize" : "/v1/payments";
  const response = http.post(`${baseUrl}${path}`, JSON.stringify(body), {
    headers: headers(merchant, key, true),
  });
  record(response, mock ? [200, 402, 429, 503] : [200, 202, 402, 409, 429, 503]);

  let payload = null;
  try {
    payload = response.json();
  } catch (error) {
    payload = null;
  }

  // Glossary: authorized, captured under auto-capture, and declined are completed.
  // A manual create is authorized; a two-call auto create ends captured.
  const state = payload && payload.state ? payload.state : "";
  if (!mock
      && ((response.status === 200 && (state === "AUTHORIZED" || state === "CAPTURED"))
          || response.status === 402)) {
    completed.add(1);
  }
  remember(
    recent,
    {
      key,
      id: payload && payload.id ? payload.id : null,
      merchant,
      amount,
      captureMode,
      body,
    },
    recentLimit,
  );
  check(response, {
    "payment answered or cleanly refused": (result) =>
      [200, 202, 402, 409, 422, 429, 503].includes(result.status),
  });
}

function status() {
  const entry = pickRecent(recent);
  if (!entry) {
    payment();
    return;
  }

  const path = mock
    ? `/inquire/${entry.key}`
    : `/v1/payments/${entry.id || "00000000-0000-7000-8000-000000000000"}`;
  const response = http.get(`${baseUrl}${path}`, {
    headers: headers(entry.merchant, null, false),
  });
  record(response, [200, 404, 429, 503]);
}

function refund() {
  const target = pickRefundTarget();
  if (!target) {
    payment();
    return;
  }

  const key = uuidv4();
  const amount = Math.max(1, Math.floor(target.amount / 10));
  const path = mock ? "/refund" : `/v1/payments/${target.id}/refunds`;
  const body = { amount_minor: amount, currency: "USD" };
  const response = http.post(`${baseUrl}${path}`, JSON.stringify(body), {
    headers: headers(target.merchant, key, true),
  });
  record(response, mock ? [200, 402, 429, 503] : [200, 202, 402, 404, 409, 422, 429, 503]);
}

function retry() {
  const entry = pickRecent(recent);
  if (!entry) {
    payment();
    return;
  }

  const path = mock ? "/authorize" : "/v1/payments";
  const response = http.post(`${baseUrl}${path}`, JSON.stringify(entry.body), {
    headers: headers(entry.merchant, entry.key, true),
  });
  record(response, mock ? [200, 402, 429, 503] : [200, 202, 402, 409, 429, 503]);
}

function other() {
  if (mock) {
    const entry = pickRecent(recent);
    const path = entry ? `/inquire/${entry.key}` : "/health";
    const response = http.get(`${baseUrl}${path}`, {
      headers: headers(entry ? entry.merchant : "merchant-0", null, false),
    });
    record(response, [200, 404, 429, 503]);
    return;
  }

  const entry = pickRecent(recent);
  if (!entry || !entry.id) {
    status();
    return;
  }

  const key = uuidv4();
  const response = http.post(
    `${baseUrl}/v1/payments/${entry.id}/cancel`,
    JSON.stringify({}),
    { headers: headers(entry.merchant, key, true) },
  );
  record(response, [200, 202, 404, 409, 429, 503]);
}

function pickRefundTarget() {
  if (refundTargets.length > 0) {
    const item = refundTargets[Math.floor(Math.random() * refundTargets.length)];
    return { id: item.id, merchant: item.merchant_id, amount: item.amount_minor };
  }
  const entry = pickRecent(recent);
  return entry && entry.id ? entry : null;
}

function record(response, expected) {
  if (response.status === 429 || response.status === 503) {
    rejected.add(1);
    return;
  }

  if (!expected.includes(response.status)) {
    unexpected.add(1);
    return;
  }

  unexpected.add(0);
}
