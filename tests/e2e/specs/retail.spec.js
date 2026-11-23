import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';

test.beforeAll(async ({ request }) => {
  await expect.poll(async () => {
    const response = await request.get('/health/ready', { timeout: 2_000 }).catch(() => null);
    return response?.ok();
  }, { timeout: 45_000, message: 'Retail API should become ready' }).toBe(true);
});

test('staff can recover a committed stock receipt after a lost reply', async ({ page }) => {
  const item = product('Notebook');
  await signIn(page, 'staff');
  await addProduct(page, item);

  let sentKey;
  await page.route('**/api/receipts', async route => {
    sentKey = route.request().postDataJSON().requestKey;
    const committed = await route.fetch();
    expect(committed.status()).toBe(201);
    await route.abort('failed');
  }, { times: 1 });
  await receiveOne(page, item);
  await expect(page.getByRole('button', { name: 'Retry receipt' })).toBeVisible();
  await page.reload();
  await page.getByRole('button', { name: 'Review receipt' }).click();
  const replay = page.waitForResponse(response => matchesPost(response, '/api/receipts'));
  await page.getByRole('button', { name: 'Retry receipt' }).click();
  const response = await replay;
  expect(response.status()).toBe(200);
  expect(response.request().postDataJSON().requestKey).toBe(sentKey);
  await expectAvailable(page, item, 1);
});

test('a recovered checkout confirms payment and staff can dispatch the whole order', async ({ page, browser, baseURL }) => {
  const a = product('Lamp');
  const b = product('Shade');
  const c = product('Bulb');
  await signIn(page, 'staff');
  for (const item of [a, b, c]) {
    await addProduct(page, item);
    await receiveOne(page, item);
    await expectAvailable(page, item, 1);
  }
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByLabel('Email address')).toBeVisible();
  await signIn(page, 'customer');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const otherContext = await browser.newContext({ baseURL, viewport: { width: 360, height: 800 } });
  try {
    const other = await otherContext.newPage();
    await signIn(other, 'customer');
    for (const item of [a, b]) await addToBasket(page, item);
    for (const item of [c, a]) await addToBasket(other, item);
    const basketLink = page.getByRole('link', { name: /View basket/ });
    await expect(basketLink).toBeInViewport();
    await basketLink.click();
    await expect(page.getByRole('heading', { name: 'Basket', exact: true })).toBeFocused();

    let committedOrder;
    await page.route('**/api/orders', async route => {
      const response = await route.fetch();
      expect(response.status()).toBe(201);
      committedOrder = await response.json();
      await route.abort('failed');
    }, { times: 1 });
    await page.getByRole('button', { name: 'Submit order' }).click();
    await expect(page.getByRole('button', { name: 'Retry checkout' })).toBeVisible();
    await page.reload();
    const replay = page.waitForResponse(response => matchesPost(response, '/api/orders'));
    await page.getByRole('button', { name: 'Retry checkout' }).click();
    const response = await replay;
    expect(response.status()).toBe(200);
    expect((await response.json()).orderId).toBe(committedOrder.orderId);
    const confirmedOrder = page.getByRole('listitem').filter({ hasText: `Order ${committedOrder.orderId}` });
    await expect(confirmedOrder.getByText('Confirmed', { exact: true })).toBeVisible({ timeout: 15_000 });
    await confirmedOrder.getByRole('button', { name: 'Copy reference' }).click();
    await expect(confirmedOrder.getByText('Reference copied.')).toBeVisible();
    const copiedReference = await page.evaluate(() => navigator.clipboard.readText());
    expect(copiedReference).toBe(committedOrder.orderId);
    await expectAvailable(page, a, 0);
    await expectAvailable(page, b, 0);
    await page.getByLabel('In stock only').check();
    await expect(page.getByRole('heading', { name: 'No matching products' })).toBeVisible();
    await page.getByRole('button', { name: 'Clear filters' }).click();
    await expect(page.getByLabel('In stock only')).not.toBeChecked();

    const rejected = other.waitForResponse(response => matchesPost(response, '/api/orders'));
    await other.getByRole('button', { name: 'Submit order' }).click();
    const rejectedResponse = await rejected;
    expect(rejectedResponse.status()).toBe(201);
    expect((await rejectedResponse.json()).rejectionReason).toBe('insufficient_stock');
    await expect(other.getByText('Rejected · insufficient stock', { exact: true }).first()).toBeVisible();
    await expectAvailable(other, c, 1);

    await other.getByRole('button', { name: 'Sign out' }).click();
    await expect(other.getByLabel('Email address')).toBeVisible();
    await signIn(other, 'staff');
    await other.getByRole('link', { name: 'Orders', exact: true }).click();
    await other.getByRole('searchbox', { name: 'Search by order reference or email' }).fill(copiedReference);
    await other.getByRole('button', { name: 'Search', exact: true }).click();
    const review = other.getByRole('button', { name: `Review order ${committedOrder.orderId}`, exact: true });
    await expect(review).toBeInViewport();
    expect(await other.getByRole('table', { name: 'Orders' }).evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await review.click();
    const detail = other.getByRole('dialog', { name: 'Order details' });
    await expect(detail.getByRole('region', { name: 'Payment', exact: true })).toContainText('Approved');
    await expect(detail.getByText(`Checkout reservation · 1 × ${a.sku}`, { exact: true })).toBeVisible();
    await detail.getByRole('button', { name: 'Dispatch order', exact: true }).click();
    await detail.getByRole('button', { name: 'Confirm dispatch', exact: true }).click();
    await expect(detail.getByText('Dispatch recorded.', { exact: true })).toBeVisible();
    await expect(detail.getByText(`Order dispatched · 1 × ${a.sku}`, { exact: true })).toBeVisible();
    await expect(detail.getByText(`Order dispatched · 1 × ${b.sku}`, { exact: true })).toBeVisible();
    await expect(detail.getByRole('button', { name: 'Dispatch order', exact: true })).toHaveCount(0);
    const timeline = detail.getByRole('region', { name: 'Order timeline' });
    const refresh = timeline.getByRole('button', { name: 'Refresh details' });
    await expect(async () => {
      if (await refresh.isVisible()) await refresh.click();
      await expect(timeline.getByRole('listitem').filter({ hasText: 'Order dispatched' })).toHaveCount(1, { timeout: 500 });
    }).toPass({ timeout: 15000 });

    await page.getByRole('button', { name: 'Refresh orders', exact: true }).click();
    await expect(confirmedOrder.getByText('Dispatched', { exact: true })).toBeVisible();
  } finally {
    await otherContext.close();
  }
});

test('dark mode persists and catalog pages follow the search filter', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await signIn(page, 'staff');
  const themeSwitch = page.getByRole('switch', { name: 'Dark mode' });
  await themeSwitch.click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.reload();
  await expect(themeSwitch).toBeChecked();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  const session = await page.request.get('/api/session');
  const { csrfToken } = await session.json();
  const marker = product('Pagination').sku;
  for (let index = 1; index <= 7; index++) {
    const response = await page.request.post('/api/products', {
      headers: { 'X-CSRF-TOKEN': csrfToken },
      data: { sku: `${marker}-${index}`, name: `Pagination product ${index}`, unitPriceMinor: 125, active: true }
    });
    expect(response.status()).toBe(201);
  }
  await page.reload();
  await search(page, marker);
  const pages = page.getByRole('navigation', { name: 'Product pages' });
  await expect(pages).toContainText('1–6 of 7');
  await pages.getByRole('button', { name: 'Next' }).click();
  await expect(pages).toContainText('7–7 of 7');
  await expect(pages.getByRole('button', { name: 'Next' })).toBeDisabled();
  await expect(page.getByText(`${marker}-7`, { exact: true })).toBeVisible();
  await search(page, `${marker}-1`);
  await expect(pages).toHaveCount(0);
  await expect(page.getByText(`${marker}-1`, { exact: true })).toBeVisible();
  await themeSwitch.click();
  await expect(themeSwitch).not.toBeChecked();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});

function product(label) {
  const suffix = randomUUID().slice(0, 8).toUpperCase();
  return { sku: `E2E-${suffix}`, name: `${label} ${suffix}` };
}

async function signIn(page, role) {
  await page.goto('/');
  await page.getByLabel('Email address').fill(`${role}@demo.local`);
  await page.getByLabel('Password', { exact: true }).fill(process.env[`DEMO_${role.toUpperCase()}_PASSWORD`]);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { name: role === 'staff' ? 'Inventory' : 'Product catalog' })).toBeVisible();
}

async function addProduct(page, item) {
  await page.getByRole('button', { name: 'Add product' }).click();
  await page.getByLabel('Product name').fill(item.name);
  await page.getByLabel('SKU', { exact: true }).fill(item.sku);
  await page.getByLabel('Unit price (USD)', { exact: true }).fill('12.50');
  await page.getByRole('button', { name: 'Save product' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await search(page, item.sku);
  await expect(page.getByText(item.sku, { exact: true })).toBeVisible();
}

async function receiveOne(page, item) {
  await page.getByRole('button', { name: 'Receive stock' }).click();
  await page.getByRole('dialog').getByRole('combobox', { name: 'Product', exact: true }).selectOption(item.sku);
  await page.getByLabel('Quantity received').fill('1');
  await page.getByLabel('Reason', { exact: true }).fill('Browser test');
  await page.getByRole('button', { name: 'Record receipt' }).click();
}

async function addToBasket(page, item) {
  await search(page, item.sku);
  await page.getByRole('button', { name: `Add ${item.name} to basket` }).click();
}

async function expectAvailable(page, item, units) {
  await search(page, item.sku);
  const row = page.getByRole('row').filter({ has: page.getByText(item.sku, { exact: true }) });
  await expect(row.getByRole('cell', { name: `${units} units available`, exact: true })).toBeVisible();
}

async function search(page, value) {
  await page.getByRole('searchbox', { name: 'Search by name or SKU' }).fill(value);
}

function matchesPost(response, path) {
  return response.request().method() === 'POST' && new URL(response.url()).pathname === path;
}
