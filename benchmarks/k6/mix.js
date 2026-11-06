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
const twoCallShare = Number(__ENV.TWO_CALL_SHARE || 0.2);
const prefillPerVu = Number(__ENV.PREFILL_PER_VU || 3);
const preallocatedVus = Math.max(10, Math.ceil(rate * 0.3));
const refundTargetsPath = __ENV.REFUND_TARGETS || "";

// D47: 50% of requests create a payment. Of those, twoCallShare are two-call
// (authorize, then a later standalone capture); the rest are one-call sales.
const saleTarget = 0.5 * (1 - twoCallShare);
const authorizeTarget = 0.5 * twoCallShare;
const captureTarget = 0.5 * twoCallShare;
const statusTarget = 0.15;
const refundTarget = 0.10;
const retryTarget = 0.10;
const otherTarget = 0.05;

const refundTargets = refundTargetsPath
  ? new SharedArray("refund targets", () => JSON.parse(open(refundTargetsPath)))
  : [];

const admitted = new Counter("k6_requests_admitted_total");
const rejected = new Counter("k6_requests_rejected_total");
const completed = new Counter("k6_payments_completed_total");
const unexpected = new Rate("op_unexpected_error");
const saleRequests = new Counter("k6_sale_requests_total");
const authorizeRequests = new Counter("k6_authorize_requests_total");
const captureRequests = new Counter("k6_capture_requests_total");
const statusRequests = new Counter("k6_status_requests_total");
const refundRequests = new Counter("k6_refund_requests_total");
const retryRequests = new Counter("k6_retry_requests_total");
const otherRequests = new Counter("k6_other_requests_total");
const captureSubstitutions = new Counter("k6_capture_substitutions_total");

const recent = [];
const recentLimit = 128;
const captureQueue = [];
let queueInitialized = false;

export const options = {
  discardResponseBodies: false,
  scenarios: {
    mix: {
      executor: "ramping-arrival-rate",
      startRate: 1,
      timeUnit: "1s",
      preAllocatedVUs: preallocatedVus,
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

// A capture consumes an authorized payment from the per-VU queue. Seed that queue
// before the load so step 0 can exercise captures without waiting for a random
// authorize to land first. These are real authorized payments, so they count as
// completed payments too.
export function setup() {
  if (mock) {
    return [];
  }

  const count = Math.min(200, prefillPerVu * preallocatedVus);
  const requests = [];
  for (let index = 0; index < count; index++) {
    const amount = 100 + Math.floor(Math.random() * 9900);
    requests.push({
      method: "POST",
      url: `${baseUrl}/v1/payments`,
      body: JSON.stringify(bodyFor(amount, "manual")),
      params: { headers: headers("merchant-0", `prefill-${uuidv4()}`, true) },
    });
  }

  const responses = http.batch(requests);
  const seeded = [];
  responses.forEach((response, index) => {
    if (response.status !== 200) {
      return;
    }
    let payload = null;
    try {
      payload = response.json();
    } catch (error) {
      payload = null;
    }
    if (!payload || payload.state !== "AUTHORIZED" || !payload.id) {
      return;
    }
    completed.add(1);
    seeded.push({
      id: payload.id,
      merchant: "merchant-0",
      amount: payload.amount_minor,
    });
  });
  return seeded;
}

export default function (prefill) {
  if (!queueInitialized) {
    const perVu = Math.ceil((prefill ? prefill.length : 0) / preallocatedVus);
    const start = (__VU - 1) * perVu;
    if (perVu > 0) {
      captureQueue.push(...prefill.slice(start, start + perVu));
    }
    queueInitialized = true;
  }

  admitted.add(1);
  const roll = Math.random();
  if (roll < saleTarget) {
    sale();
  } else if (roll < saleTarget + authorizeTarget) {
    authorize();
  } else if (roll < saleTarget + authorizeTarget + captureTarget) {
    capture();
  } else if (roll < saleTarget + authorizeTarget + captureTarget + statusTarget) {
    status();
  } else if (roll < saleTarget + authorizeTarget + captureTarget + statusTarget + refundTarget) {
    refund();
  } else if (roll < saleTarget + authorizeTarget + captureTarget + statusTarget + refundTarget + retryTarget) {
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

function sale() {
  saleRequests.add(1);
  createPayment("auto");
}

function authorize() {
  authorizeRequests.add(1);
  createPayment("manual");
}

function createPayment(captureMode) {
  const merchant = pickMerchant();
  const key = uuidv4();
  const amount = 100 + Math.floor(Math.random() * 9900);
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

  const state = payload && payload.state ? payload.state : "";
  if (response.status === 200) {
    if (mock) {
      completed.add(1);
    } else if (state === "AUTHORIZED" || state === "CAPTURED") {
      completed.add(1);
      if (state === "AUTHORIZED" && payload.id) {
        enqueue({
          id: payload.id,
          merchant,
          amount: payload.amount_minor,
        });
      }
    }
  } else if (response.status === 402) {
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

function capture() {
  if (mock) {
    captureRequests.add(1);
    const key = uuidv4();
    const response = http.post(
      `${baseUrl}/capture`,
      JSON.stringify({ amount_minor: 1000, currency: "USD" }),
      { headers: headers("merchant-0", key, true) },
    );
    record(response, [200, 402, 429, 503]);
    return;
  }

  const entry = captureQueue.shift();
  if (!entry) {
    captureSubstitutions.add(1);
    authorize();
    return;
  }

  captureRequests.add(1);
  const response = http.post(
    `${baseUrl}/v1/payments/${entry.id}/capture`,
    JSON.stringify({ amount_minor: entry.amount }),
    {
      headers: headers(entry.merchant, uuidv4(), true),
      tags: { name: "POST /v1/payments/:id/capture" },
    },
  );
  record(response, [200, 202, 402, 409, 422, 429, 503]);

  let payload = null;
  try {
    payload = response.json();
  } catch (error) {
    payload = null;
  }
  if (response.status !== 200 && payload && payload.state === "AUTHORIZED") {
    enqueue(entry);
  }
}

function enqueue(entry) {
  if (captureQueue.length < 256) {
    captureQueue.push(entry);
  }
}

function status() {
  statusRequests.add(1);
  const entry = pickRecent(recent);
  if (!entry) {
    sale();
    return;
  }

  const path = mock
    ? `/inquire/${entry.key}`
    : `/v1/payments/${entry.id || "00000000-0000-7000-8000-000000000000"}`;
  const response = http.get(`${baseUrl}${path}`, {
    headers: headers(entry.merchant, null, false),
    tags: { name: mock ? "GET /inquire/:key" : "GET /v1/payments/:id" },
  });
  record(response, [200, 404, 429, 503]);
}

function refund() {
  refundRequests.add(1);
  const target = pickRefundTarget();
  if (!target) {
    sale();
    return;
  }

  const key = uuidv4();
  const amount = Math.max(1, Math.floor(target.amount / 10));
  const path = mock ? "/refund" : `/v1/payments/${target.id}/refunds`;
  const body = { amount_minor: amount, currency: "USD" };
  const response = http.post(`${baseUrl}${path}`, JSON.stringify(body), {
    headers: headers(target.merchant, key, true),
    tags: { name: mock ? "POST /mock/refund" : "POST /v1/payments/:id/refunds" },
  });
  record(response, mock ? [200, 402, 429, 503] : [200, 202, 402, 404, 409, 422, 429, 503]);
}

function retry() {
  retryRequests.add(1);
  const entry = pickRecent(recent);
  if (!entry) {
    sale();
    return;
  }

  const path = mock ? "/authorize" : "/v1/payments";
  const response = http.post(`${baseUrl}${path}`, JSON.stringify(entry.body), {
    headers: headers(entry.merchant, entry.key, true),
  });
  record(response, mock ? [200, 402, 429, 503] : [200, 202, 402, 409, 429, 503]);
}

function other() {
  otherRequests.add(1);
  if (mock) {
    const entry = pickRecent(recent);
    const path = entry ? `/inquire/${entry.key}` : "/health";
    const response = http.get(`${baseUrl}${path}`, {
      headers: headers(entry ? entry.merchant : "merchant-0", null, false),
      tags: { name: entry ? "GET /inquire/:key" : "GET /health" },
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
    {
      headers: headers(entry.merchant, key, true),
      tags: { name: "POST /v1/payments/:id/cancel" },
    },
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
