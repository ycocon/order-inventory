import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { baseURL, compose, demo, newSession, post, project, scalar, until } from './payment-harness.js';

const simulator = 'http://' + compose('port', 'simulator', '8080');
async function ready(url) {
  await until(async () => {
    try { return (await fetch(url + '/health/ready', { signal: AbortSignal.timeout(1500) })).ok; }
    catch { return false; }
  }, 'Service failed to become ready');
}
await ready(baseURL);
const staff = await newSession('staff@demo.local', demo.DEMO_STAFF_PASSWORD);
const customer = await newSession('customer@demo.local', demo.DEMO_CUSTOMER_PASSWORD);
async function prepare(quantity) {
  const sku = 'REC-' + randomUUID().replaceAll('-', '').toUpperCase();
  await post(staff, '/api/products', { sku, name: 'Recovery fixture', unitPriceMinor: 1250, active: true }, 201);
  await post(staff, '/api/receipts', { sku, quantity, reason: 'Recovery fixture', requestKey: randomUUID() }, 201);
  return sku;
}
async function buy(sku) {
  const data = { requestKey: randomUUID(), lines: [{ sku, quantity: 1 }] };
  const { orderId } = await post(customer, '/api/orders', data, 201);
  const operation = scalar('retail', `SELECT operation_id FROM payment_intents WHERE order_id='${orderId}'`);
  return { orderId, operation, data };
}
const balance = sku => scalar('retail', `SELECT on_hand,reserved FROM stock_positions WHERE sku='${sku}'`);
const decision = id => scalar('payment_simulator', `SELECT decision FROM payment_authorizations WHERE operation_id='${id}'`);
async function detail(orderId) {
  const r = await staff.client.get('/api/staff/orders/' + orderId);
  assert.equal(r.status(), 200);
  return r.json();
}
async function confirmed(order) {
  const view = await until(async () => {
    const v = await detail(order.orderId);
    return v.order.status === 'confirmed' && !v.timelinePending && v;
  }, 'Order did not recover', 120000);
  assert.equal(view.payment.operationId, order.operation);
  assert.equal(view.payment.workStatus, 'complete');
  assert.deepEqual(view.history.map(e => e.kind), ['OrderAwaitingPayment', 'OrderConfirmed']);
  assert.equal(view.stock.length, 1);
  assert.equal(decision(order.operation), 'approved');
}
async function interrupted(point, stale = false) {
  const sku = await prepare(1);
  compose('exec','-T','retail','touch','/tmp/' + point);
  const order = await buy(sku);
  const marker = '/tmp/' + point + '.paused';
  await until(() => compose('exec','-T','retail','sh','-c',`if test -f ${marker}; then echo paused; fi`), 'Checkpoint missed');
  assert.equal(decision(order.operation), 'approved');
  assert.equal(balance(sku),'1|1');
  const before = await detail(order.orderId);
  assert.equal(before.order.status, point === 'before-finish' ? 'awaiting_payment' : 'confirmed');
  if (stale) {
    const peer = project + '-replacement';
    compose('run','--name',peer,'--rm','-d','--no-deps','retail');
    try {
      await confirmed(order);
      compose('exec','-T','retail','unlink',marker);
      await until(() => compose('logs','--tail','200','retail').includes('Ignored stale payment claim ' + order.operation), 'Old worker did not return');
    } finally { execFileSync('docker',['stop',peer]); }
  } else {
    compose('kill','retail');
    compose('start','retail');
    await ready(baseURL);
    compose('exec','-T','retail','unlink',marker);
  }
  await confirmed(order);
  assert.equal(balance(sku),'1|1');
  const replay = await post(customer,'/api/orders',order.data,200);
  assert.equal(replay.orderId,order.orderId);
  await confirmed(order);
  console.log(JSON.stringify({ pass: stale ? 'stale worker' : point, orderId: order.orderId, payment: order.operation,
    before: before.order.status, after: 'confirmed', balancesBeforeAndAfter: '1 on hand, 1 reserved', approvals: 1 }));
}
async function outage(exhaust) {
  const count = exhaust ? 1 : 10;
  const sku = await prepare(count);
  compose('stop','simulator');
  const orders = [];
  for (let i=0;i<count;i++) orders.push(await buy(sku));
  if (exhaust) {
    await until(async () => (await detail(orders[0].orderId)).payment.workStatus === 'overdue', 'Retries did not exhaust', 210000);
    const attention = await (await staff.client.get('/api/staff/orders?status=overdue&q=' + orders[0].orderId)).json();
    assert.equal(attention.total,1);
    const view = await detail(orders[0].orderId);
    assert.equal(view.payment.attemptCount,8);
    assert.ok(view.payment.lastError);
  } else await delay(60000);
  for (const order of orders) assert.equal((await detail(order.orderId)).order.status,'awaiting_payment');
  assert.equal(balance(sku),count + '|' + count);
  compose('start','simulator');
  await ready(simulator);
  const start = performance.now();
  if (exhaust) {
    const path = '/api/staff/orders/' + orders[0].orderId + '/resume-payment';
    assert.equal((await customer.client.post(path,{headers:customer.headers})).status(),403);
    const statuses = await Promise.all([1,2].map(async () => (await staff.client.post(path,{headers:staff.headers})).status()));
    assert.deepEqual(statuses.sort(),[204,409]);
  }
  await Promise.all(orders.map(confirmed));
  const elapsed = (performance.now()-start)/1000;
  assert.ok(elapsed < 120);
  assert.equal(balance(sku),count + '|' + count);
  console.log(JSON.stringify({ pass: exhaust ? 'resume after eight attempts' : '60-second outage', confirmed: count,
    secondsFromReady: +elapsed.toFixed(2), balancesBeforeAndAfter: balance(sku) }));
}
try {
  await interrupted('before-finish');
  // The first test uses dropped replies. The remaining cases use ordinary approvals.
  process.env.SIMULATOR_DROP_NEW_REPLIES = 'false';
  compose('up','-d','--no-deps','simulator');
  await ready(simulator);
  await interrupted('after-finish');
  await interrupted('before-finish',true);
  await outage(false);
  await outage(true);
} finally {
  await Promise.all([staff.client.dispose(),customer.client.dispose()]);
}

