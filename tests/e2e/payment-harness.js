import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { parseEnv } from 'node:util';
import { request } from '@playwright/test';

export const project = process.argv[2];
assert.match(project ?? '', /^order-inventory-payment-test-[a-f0-9]+$/);
export const demo = parseEnv(readFileSync(new URL('../../.env', import.meta.url), 'utf8'));
export function compose(...args) {
  return execFileSync('docker', ['compose', '-p', project, ...args], { encoding: 'utf8' }).trim();
}

export function scalar(database, sql) {
  return compose('exec', '-T', 'db', 'psql', '--no-psqlrc', '-U', 'postgres', '-d', database,
    '-Atq', '-v', 'ON_ERROR_STOP=1', '-c', sql);
}

export const baseURL = 'http://' + compose('port', 'retail', '8080');

export async function until(check, label, timeout = 45_000) {
  const deadline = Date.now() + timeout;
  do {
    const result = await check();
    if (result) return result;
    await delay(200);
  } while (Date.now() < deadline);
  throw new Error(label);
}

export async function newSession(email, password) {
  const client = await request.newContext({ baseURL });
  try {
    const anonymous = await (await client.get('/api/session')).json();
    const login = await client.post('/api/login', { data: { email, password },
      headers: { 'X-CSRF-TOKEN': anonymous.csrfToken } });
    assert.equal(login.status(), 204, 'Demo sign-in');
    const signedIn = await (await client.get('/api/session')).json();
    return { client, headers: { 'X-CSRF-TOKEN': signedIn.csrfToken } };
  } catch (error) {
    await client.dispose();
    throw error;
  }
}

export async function post(session, route, data, status) {
  const response = await session.client.post(route, { data, headers: session.headers });
  assert.equal(response.status(), status, route);
  return response.json();
}

export async function waitForOrder(session, orderId) {
  return until(async () => {
    const response = await session.client.get(`/api/orders/${orderId}`);
    assert.equal(response.status(), 200);
    const order = await response.json();
    return order.status === 'awaiting_payment' ? null : order;
  }, 'Payment outcome did not reach the order');
}

