import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { baseURL, compose, demo, newSession, post, project, scalar, until } from './payment-harness.js';

await until(async () => {
  try { return (await fetch(baseURL + '/health/ready', {signal: AbortSignal.timeout(1500)})).ok; }
  catch { return false; }
}, 'Retail did not become ready');
const staff = await newSession('staff@demo.local', demo.DEMO_STAFF_PASSWORD);
const customer = await newSession('customer@demo.local', demo.DEMO_CUSTOMER_PASSWORD);
try {
  const cookies = (await customer.client.storageState()).cookies.map(c => c.name + '=' + c.value).join('; ');
  const runs = [[20,300],[100,30]].filter(([rate]) =>
    !process.argv[3] || process.argv[3] === 'both' || process.argv[3] === (rate === 20 ? 'steady' : 'burst'));
  assert.ok(runs.length, 'Choose steady, burst, or both');
  console.log(JSON.stringify({
    docker: execFileSync('docker', ['info', '--format','{{.ServerVersion}}|{{.NCPU}}|{{.MemTotal}}'], {encoding:'utf8'}).trim(),
    retailImage: execFileSync('docker', ['inspect', '--format','{{.Image}}', compose('ps','-q','retail')], {encoding:'utf8'}).trim(),
    k6: '1.4.2'
  }));
  for (const [rate, seconds] of runs) {
    const sku = 'LOAD-' + randomUUID().replaceAll('-','').toUpperCase();
    const quantity = rate * seconds;
    await post(staff,'/api/products',{ sku, name:'Baseline fixture', unitPriceMinor:1250, active:true },201);
    await post(staff,'/api/receipts',{ sku, quantity, reason:'Performance baseline', requestKey:randomUUID() },201);
    const script = fileURLToPath(new URL('../load/checkout.js',import.meta.url));
    const output = execFileSync('docker',['run','--rm','--network',project + '_default','-v',script + ':/load.js:ro',
      ...['COOKIES','CSRF','SKU','RATE','SECONDS'].flatMap(key => ['-e',key]),
      'grafana/k6:1.4.2','run','--quiet','/load.js'],{encoding:'utf8', stdio:['ignore','pipe','inherit'],
      env:{...process.env, COOKIES:cookies, CSRF:customer.headers['X-CSRF-TOKEN'], SKU:sku, RATE:String(rate), SECONDS:String(seconds)}});
    const metrics = JSON.parse(output);
    console.log(JSON.stringify({ rate, seconds, sku, ...metrics }));
    const from = `FROM customer_orders o JOIN order_lines l ON o.id=l.order_id WHERE l.sku='${sku}'`;
    const end = `to_timestamp(${metrics.ended}/1000.0)`;
    await until(() => Number(scalar('retail',`SELECT count(*) ${from} AND o.status='Confirmed'`)) === metrics.admitted,
      'Admitted orders did not drain', 600000);
    const eventFilter = `FROM order_events e JOIN order_lines l USING (order_id)
      LEFT JOIN order_timeline t ON t.event_id=e.id
      LEFT JOIN order_outbox b ON b.event_id=e.id WHERE l.sku='${sku}'`;
    await until(() => scalar('retail',`SELECT count(*) ${eventFilter}
      AND (t.event_id IS NULL OR b.published_at IS NULL)`) === '0', 'Timeline did not drain', 600000);
    const [events, missingAtEnd, timelineP95, timelineDrain, mismatches] = scalar('retail',`
      SELECT count(*), count(*) FILTER(WHERE t.delivered_at > ${end}),
        round(extract(epoch FROM percentile_cont(0.95) WITHIN GROUP (ORDER BY t.delivered_at-e.recorded_at))::numeric,3),
        greatest(0,round(extract(epoch FROM max(t.delivered_at)-${end})::numeric,3)),
        count(*) FILTER(WHERE (t.order_id,t.version,t.kind,t.recorded_at,t.actor_id)
          IS DISTINCT FROM (e.order_id,e.version,e.kind,e.recorded_at,e.actor_id))
      ${eventFilter}`).split('|').map(Number);
    const amounts = scalar('retail',`SELECT on_hand,reserved,(SELECT sum(quantity) FROM stock_holds WHERE sku='${sku}' AND resolved_at IS NULL),
      (SELECT count(*) FROM stock_movements WHERE sku='${sku}' AND kind='reserve') FROM stock_positions WHERE sku='${sku}'`);
    const timings = scalar('retail',`SELECT count(*) FILTER(WHERE e.recorded_at > ${end}),
      round(extract(epoch FROM percentile_cont(0.95) WITHIN GROUP (ORDER BY e.recorded_at-o.created_at))::numeric,3),
      greatest(0,round(extract(epoch FROM max(e.recorded_at)-${end})::numeric,3)),
      coalesce(round(max(extract(epoch FROM ${end}-o.created_at)) FILTER(WHERE e.recorded_at > ${end})::numeric,3),0)
      FROM customer_orders o JOIN order_lines l ON o.id=l.order_id JOIN order_events e ON e.order_id=o.id AND e.kind='OrderConfirmed'
      WHERE l.sku='${sku}'`).split('|');
    const retailPayments = scalar('retail',`
      SELECT i.operation_id,i.amount_minor,i.currency,i.decision FROM payment_intents i JOIN order_lines l USING(order_id)
      WHERE l.sku='${sku}' ORDER BY i.operation_id`);
    const simulatorPayments = scalar('payment_simulator',`
      SELECT operation_id,amount_minor,currency,decision FROM payment_authorizations ORDER BY operation_id`);
    // Both workloads share an isolated simulator, so retain only the operation IDs for this SKU.
    const paymentIds = new Set(retailPayments.split('\n').filter(Boolean).map(row => row.split('|')[0]));
    const approvals = simulatorPayments.split('\n').filter(row => paymentIds.has(row.split('|')[0]));
    const problems = [];
    if (approvals.length !== metrics.admitted || approvals.join('\n') !== retailPayments
        || approvals.some(row => !row.endsWith('|approved'))) problems.push('payment reconciliation');
    if (amounts !== [quantity,metrics.admitted,metrics.admitted,metrics.admitted].join('|')) problems.push('stock reconciliation');
    if (events !== 2 * metrics.admitted || mismatches !== 0) problems.push('timeline reconciliation');
    if (metrics.errors || metrics.admitted + metrics.rejected !== metrics.sent) problems.push('unexpected checkout responses');
    if (metrics.sent !== quantity || metrics.dropped) problems.push('incomplete k6 workload');
    if (rate === 20 && (metrics.admitted < 5940 || metrics.submissionP95ms > 500)) problems.push('steady traffic target');
    console.log(JSON.stringify({ sku, paymentApprovals:approvals.length, balances:amounts, pendingAtEnd:Number(timings[0]),
      completionP95seconds:Number(timings[1]), drainSeconds:Number(timings[2]), oldestPendingSeconds:Number(timings[3]),
      timelineEntries:events, timelinePendingAtEnd:missingAtEnd, timelineLagP95seconds:timelineP95, timelineDrainSeconds:timelineDrain,
      problems }));
    if (problems.length) {
      process.exitCode = 1;
    }
  }
} finally { await Promise.all([staff.client.dispose(), customer.client.dispose()]); }

