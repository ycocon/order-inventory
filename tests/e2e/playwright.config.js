import { defineConfig } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { parseEnv } from 'node:util';

const dotenv = new URL('../../.env', import.meta.url);
const local = existsSync(dotenv) ? parseEnv(readFileSync(dotenv, 'utf8')) : {};
for (const name of ['DEMO_STAFF_PASSWORD', 'DEMO_CUSTOMER_PASSWORD']) {
  if (!process.env[name] && local[name]) process.env[name] = local[name];
  if (!process.env[name])
    throw new Error(`Set ${name} or initialize the local demo with demo.ps1.`);
}

export default defineConfig({
  testDir: './specs',
  workers: 1,
  timeout: 90_000,
  globalTimeout: 240_000,
  forbidOnly: Boolean(process.env.CI),
  use: { baseURL: process.env.E2E_BASE_URL ?? `http://127.0.0.1:${local.RETAIL_PORT ?? 5188}` }
});
