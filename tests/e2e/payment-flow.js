import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { baseURL, demo, newSession, post, scalar, until, waitForOrder } from './payment-harness.js';
const scenario = process.argv[3];
assert.ok(['race','decline','lost'].includes(scenario));
const suffix = randomUUID().replaceAll('-', '');
const sku = ('PAY-' + suffix).toUpperCase();

async function seedBuyers() {
  // This fixture is confined to the disposable Compose project above.
  scalar('retail', `
    WITH users AS (
      SELECT 'buyer-${suffix}-' || n || '@demo.local' AS email, gen_random_uuid()::text AS id
      FROM generate_series(1, 100) n
    ), inserted AS (
      INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
        "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
        "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
      SELECT id, email, upper(email), email, upper(email), true,
        (SELECT "PasswordHash" FROM "AspNetUsers" WHERE "Email" = 'customer@demo.local'),
        gen_random_uuid()::text, gen_random_uuid()::text, false, false, true, 0
      FROM users RETURNING "Id"
    )
    INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
    SELECT inserted."Id", roles."Id" FROM inserted
    CROSS JOIN "AspNetRoles" roles WHERE roles."NormalizedName" = 'CUSTOMER';`);
  const buyers = [];
  for (let first = 1; first <= 100; first += 10) {
    const batch = await Promise.all(Array.from({ length: 10 }, (_, offset) =>
      newSession(`buyer-${suffix}-${first + offset}@demo.local`, demo.DEMO_CUSTOMER_PASSWORD)));
    buyers.push(...batch);
  }
  return buyers;
}

await until(async () => {
  try { return (await fetch(baseURL + '/health/ready', { signal: AbortSignal.timeout(1_500) })).ok; }
  catch { return false; }
}, 'Retail API did not become ready');
const staff = await newSession('staff@demo.local', demo.DEMO_STAFF_PASSWORD);
const customers = [];
try {
  await post(staff, '/api/products', { sku, name: 'Payment flow test', unitPriceMinor: 1250, active: true }, 201);
  await post(staff, '/api/receipts', { sku, quantity: 1, reason: 'Payment flow test', requestKey: randomUUID() }, 201);
  if (scenario === 'race') customers.push(...await seedBuyers());
  else customers.push(await newSession('customer@demo.local', demo.DEMO_CUSTOMER_PASSWORD));

  const basket = () => ({ requestKey: randomUUID(), lines: [{ sku, quantity: 1 }] });
  const submissions = await Promise.all(customers.map(async session => {
    const data = basket();
    return { session, data, order: await post(session, '/api/orders', data, 201) };
  }));
  const rejected = submissions.filter(({ order }) => order.rejectionReason === 'insufficient_stock');
  assert.equal(rejected.length, scenario === 'race' ? 99 : 0);
  const admitted = submissions.find(({ order }) => order.status === 'awaiting_payment');
  assert.ok(admitted, 'One basket should reserve the unit');
  const final = await waitForOrder(admitted.session, admitted.order.orderId);
  const declined = scenario === 'decline';
  assert.equal(final.status, declined ? 'rejected' : 'confirmed');
  assert.equal(final.rejectionReason, declined ? 'payment_declined' : null);
  const replay = await post(admitted.session, '/api/orders', admitted.data, 200);
  assert.deepEqual(replay, final);
  assert.equal(scalar('retail', `
    SELECT count(*) FROM payment_intents i JOIN order_lines l ON l.order_id = i.order_id WHERE l.sku = '${sku}';`), '1');
  const payment = scalar('retail', `
    SELECT i.operation_id, w.attempt_count, w.status FROM payment_intents i
    JOIN payment_work w USING (operation_id) WHERE i.order_id = '${final.orderId}';`).split('|');
  assert.equal(payment[2], 'complete');
  if (scenario === 'lost') assert.ok(Number(payment[1]) >= 2, 'The lost response should cause reconciliation');
  assert.equal(scalar('payment_simulator', `
    SELECT decision FROM payment_authorizations WHERE operation_id = '${payment[0]}';`),
    declined ? 'declined' : 'approved');
  assert.equal(scalar('retail', `
    SELECT on_hand, reserved, (SELECT count(*) FROM stock_movements WHERE sku = '${sku}' AND kind = 'release')
    FROM stock_positions WHERE sku = '${sku}';`), declined ? '1|0|1' : '1|1|0');
  console.log(scenario === 'race' ? 'PASS: 100 signed-in buyers, one confirmation and approval, 99 stock rejections.'
    : declined ? 'PASS: a decline releases stock once, even after checkout is replayed.'
    : 'PASS: a lost simulator reply recovered the original approval and preserved the hold.');
} finally {
  await Promise.all([staff, ...customers].map(session => session.client.dispose()));
}
