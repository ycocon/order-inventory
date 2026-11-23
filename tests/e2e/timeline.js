import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { baseURL, compose, demo, newSession, post, scalar, until, waitForOrder } from './payment-harness.js';

const queue = 'orders.timeline';
const relay = 'timeline-relay';
const reader = 'timeline-reader';
const management = 'http://' + compose('port', 'rabbitmq', '15672') + '/api/';
const authorization = 'Basic ' + Buffer.from('timeline:' + demo.RABBITMQ_PASSWORD).toString('base64');

async function rabbit(path, data) {
  const response = await fetch(management + path, {
    headers: { authorization, 'Content-Type': 'application/json' },
    method: data === undefined ? 'GET' : 'POST',
    body: data === undefined ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(3000)
  });
  if (response.status === 404 && data === undefined) return null;
  assert.equal(response.status, 200, path);
  return response.json();
}

await until(async () => {
  try { return (await fetch(baseURL + '/health/ready', { signal: AbortSignal.timeout(1500) })).ok; }
  catch { return false; }
}, 'Retail did not become ready');

const staff = await newSession('staff@demo.local', demo.DEMO_STAFF_PASSWORD);
const customer = await newSession('customer@demo.local', demo.DEMO_CUSTOMER_PASSWORD);
const emptySku = 'EMPTY-' + randomUUID().toUpperCase();
const sku = 'TIMELINE-' + randomUUID().toUpperCase();

const buy = (product = sku) => post(customer, '/api/orders', {
  requestKey: randomUUID(), lines: [{ sku: product, quantity: 1 }]
}, 201);
async function detail(id) {
  const response = await staff.client.get('/api/staff/orders/' + id);
  assert.equal(response.status(), 200);
  return response.json();
}
async function timeline(id, length) {
  return until(async () => {
    const result = await detail(id);
    return result.history.length === length && result;
  }, 'Timeline did not reach ' + length + ' entries for ' + id);
}
function state(eventId) {
  return scalar('retail', `SELECT published_at IS NOT NULL,
    (SELECT count(*) FROM order_timeline WHERE event_id='${eventId}')
    FROM order_outbox WHERE event_id='${eventId}'`);
}
function events(orderId) {
  return JSON.parse(scalar('retail', `SELECT json_agg(json_build_object(
    'eventId', id, 'orderId', order_id, 'kind', kind, 'version', version, 'recordedAt', recorded_at, 'actorId', actor_id)
    ORDER BY version) FROM order_events WHERE order_id='${orderId}'`));
}
async function publish(message) {
  const result = await rabbit('exchanges/%2F/amq.default/publish', {
    properties: { delivery_mode: 2, content_type: 'application/json' }, routing_key: queue,
    payload: JSON.stringify(message), payload_encoding: 'string'
  });
  assert.equal(result.routed, true);
}
function arm(service, checkpoint) {
  compose('exec','-T',service,'touch','/tmp/' + checkpoint);
}
async function killAndRestart(service, checkpoint) {
  compose('kill',service);
  compose('start',service);
  compose('exec','-T',service,'unlink','/tmp/' + checkpoint + '.paused');
}
async function paused(service, checkpoint) {
  await until(() => compose('exec','-T',service,'sh','-c',`if test -f /tmp/${checkpoint}.paused; then echo paused; fi`),
    'Worker missed ' + checkpoint);
}

try {
  await until(async () => {
    try { return (await rabbit('queues/%2F/' + queue))?.consumers === 1; }
    catch (error) {
      if (error instanceof TypeError || error.name === 'TimeoutError') return false;
      throw error;
    }
  }, 'Timeline reader did not subscribe');
  for (const newSku of [emptySku, sku]) {
    await post(staff,'/api/products',{ sku:newSku, name:'Timeline fixture', unitPriceMinor:1250, active:true },201);
  }
  await post(staff,'/api/receipts',{ sku, quantity:2, reason:'Timeline fixture', requestKey:randomUUID() },201);

  arm(relay,'after-publish');
  const first = await buy(emptySku);
  await paused(relay,'after-publish');
  const [firstEvent] = events(first.orderId);
  await timeline(first.orderId,1);
  assert.equal(state(firstEvent.eventId),'f|1');
  await killAndRestart(relay,'after-publish');
  await until(() => compose('logs','--tail','80',reader).includes('Ignored duplicate timeline event ' + firstEvent.eventId),
    'Reader did not see the republished event');
  await until(() => state(firstEvent.eventId) === 't|1', 'Relay did not mark the event sent');
  console.log(JSON.stringify({ pass:'publisher killed after confirm', event:firstEvent.eventId, timelineEntries:1 }));

  arm(reader,'after-projection');
  const second = await buy(emptySku);
  await paused(reader,'after-projection');
  const [secondEvent] = events(second.orderId);
  await timeline(second.orderId,1);
  await killAndRestart(reader,'after-projection');
  await until(() => compose('logs','--tail','80',reader).includes(
    'Ignored duplicate timeline event ' + secondEvent.eventId + ' (broker redelivery True)'), 'Broker did not redeliver the unacknowledged message');
  assert.equal(state(secondEvent.eventId),'t|1');
  console.log(JSON.stringify({ pass:'reader killed after commit', event:secondEvent.eventId, brokerRedelivery:true, timelineEntries:1 }));

  compose('stop',relay);
  const third = await buy();
  assert.equal((await waitForOrder(customer,third.orderId)).status,'confirmed');
  await post(staff,'/api/staff/orders/' + third.orderId + '/dispatch',undefined,201);
  const [placed, paid, dispatched] = events(third.orderId);
  assert.equal((await detail(third.orderId)).timelinePending,true);
  await publish(dispatched);
  assert.equal((await timeline(third.orderId,1)).history[0].kind,'OrderDispatched');
  await publish({ ...paid, version:0 });
  await publish(paid);
  let projected = await timeline(third.orderId,2);
  assert.equal(projected.history.at(-1).kind,'OrderDispatched');
  assert.equal(projected.order.status,'dispatched');
  assert.equal(projected.timelinePending,true);
  await publish(placed);
  projected = await timeline(third.orderId,3);
  assert.deepEqual(projected.history.map(e => e.kind),['OrderAwaitingPayment','OrderConfirmed','OrderDispatched']);
  assert.equal(projected.history.at(-1).actor,'staff@demo.local');
  assert.equal(projected.timelinePending,false);
  assert.ok(projected.timelineUpdatedAt);
  await until(async () => (await rabbit('queues/%2F/' + queue + '.dead'))?.messages === 1, 'Invalid event did not reach the dead-letter queue');
  console.log(JSON.stringify({ pass:'out-of-order and invalid delivery', orderId:third.orderId, versions:[3,2,1],
    finalStatus:'dispatched', deadLetters:1, timelineEntries:3 }));

  compose('start',relay);
  await until(() => scalar('retail','SELECT count(*) FROM order_outbox WHERE published_at IS NULL') === '0', 'Outbox did not drain');
  compose('stop','rabbitmq');
  const fourth = await buy();
  assert.equal((await waitForOrder(customer,fourth.orderId)).status,'confirmed');
  assert.equal((await detail(fourth.orderId)).timelinePending,true);
  for (const event of events(fourth.orderId)) assert.equal(state(event.eventId),'f|0');
  compose('start','rabbitmq');
  const recovered = await timeline(fourth.orderId,2);
  assert.equal(recovered.timelinePending,false);
  assert.equal(recovered.order.status,'confirmed');
  await until(() => scalar('retail','SELECT count(*) FROM order_outbox WHERE published_at IS NULL') === '0', 'Unsent outbox entries remained');
  assert.equal(scalar('retail', `SELECT on_hand,reserved FROM stock_positions WHERE sku='${sku}'`),'1|1');
  console.log(JSON.stringify({ pass:'broker outage', orderId:fourth.orderId, timelineEntries:2, unsent:0, onHand:1, reserved:1 }));
} finally {
  await Promise.all([staff.client.dispose(),customer.client.dispose()]);
}

