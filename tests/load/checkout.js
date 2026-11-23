import http from 'k6/http';
import execution from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';

const rate = Number(__ENV.RATE);
const seconds = Number(__ENV.SECONDS);
const preAllocatedVUs = rate === 100 ? 1800 : 60;
const maxVUs = rate === 100 ? 2200 : 240;
const accepted = new Counter('accepted_orders');
const rejected = new Counter('capacity_rejections');
const errors = new Counter('unexpected_errors');
const latency = new Trend('accepted_latency', true);
export const options = {
  scenarios: { checkout: { executor: 'constant-arrival-rate', rate, timeUnit: '1s',
    duration: seconds + 's', preAllocatedVUs, maxVUs } },
  summaryTrendStats: ['p(95)']
};
export default function () {
  // A boundary tick can schedule one extra iteration at the exact end of the window.
  if (execution.scenario.iterationInTest >= rate * seconds) return;
  const response = http.post('http://retail:8080/api/orders', JSON.stringify({
    requestKey: __ENV.SKU + '-' + execution.scenario.iterationInTest,
    lines: [{ sku: __ENV.SKU, quantity: 1 }]
  }), { headers: { 'Content-Type':'application/json', Cookie: __ENV.COOKIES, 'X-CSRF-TOKEN': __ENV.CSRF } });
  if (response.status === 201 && response.json('status') === 'awaiting_payment') {
    accepted.add(1);
    latency.add(response.timings.duration);
  } else if (response.status === 429) rejected.add(1);
  else errors.add(1);
}
export function handleSummary(data) {
  const metric = (name, field) => data.metrics[name]?.values[field] ?? 0;
  return { stdout: JSON.stringify({ ended: Date.now(), offered: rate * seconds, sent: metric('http_reqs','count'),
    preAllocatedVUs, maxVUs,
    admitted: metric('accepted_orders','count'), rejected: metric('capacity_rejections','count'),
    errors: metric('unexpected_errors','count'), dropped: metric('dropped_iterations','count'),
    submissionP95ms: metric('http_req_duration','p(95)'), admittedP95ms: metric('accepted_latency','p(95)') }) };
}

