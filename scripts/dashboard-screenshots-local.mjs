#!/usr/bin/env node
// Capture dashboard screenshots from local dev stack (no bot detection throttling)
import { chromium } from 'playwright';

const BASE = process.argv[2] || 'http://localhost:8080';
const out = '/tmp/sb-local';

const browser = await chromium.launch();
const ctx = await browser.newContext({
  viewport: { width: 1920, height: 1080 },
  extraHTTPHeaders: { 'X-SB-Api-Key': 'DEV-BYPASS' }
});
const page = await ctx.newPage();

const consoleErrors = [];
page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

async function capture(name, action) {
  try {
    await action();
    await page.waitForTimeout(1500);
    await page.screenshot({ path: `${out}-${name}.png`, fullPage: true });
    console.log(`✓ ${name}`);
  } catch (e) {
    console.log(`✗ ${name}: ${e.message}`);
  }
}

console.log(`Capturing ${BASE}/_stylobot ...`);

// 1. Overview
await capture('01-overview', async () => {
  await page.goto(`${BASE}/_stylobot`, { waitUntil: 'domcontentloaded', timeout: 15000 });
});

// 2-6. Tabs
for (const [num, tab] of [[2, 'Visitors'], [3, 'Countries'], [4, 'Sessions'],
                           [5, 'Endpoints'], [6, 'Clusters'], [7, 'User Agents']]) {
  await capture(`${String(num).padStart(2, '0')}-${tab.toLowerCase().replace(' ', '-')}`, async () => {
    const link = page.locator(`a:has-text("${tab}")`).first();
    await link.click({ timeout: 5000 });
  });
}

// Seed some traffic to populate the dashboard
console.log('Seeding traffic...');
const browser2 = await chromium.launch();
const ctx2 = await browser2.newContext({
  extraHTTPHeaders: { 'X-SB-Api-Key': 'DEV-BYPASS' }
});
const trafficPage = await ctx2.newPage();
const paths = ['/', '/api/users', '/api/orders', '/wp-admin', '/.env', '/api/search?q=test',
               '/login', '/checkout', '/api/users/123', '/product/456'];
for (let i = 0; i < 30; i++) {
  try {
    await trafficPage.goto(`${BASE}${paths[i % paths.length]}`, { timeout: 5000, waitUntil: 'domcontentloaded' });
  } catch {}
  if (i % 5 === 0) await trafficPage.waitForTimeout(500);
}
await browser2.close();
console.log('Traffic seeded');

// Revisit with data
await capture('01b-overview-with-data', async () => {
  await page.goto(`${BASE}/_stylobot`, { waitUntil: 'domcontentloaded', timeout: 15000 });
});

await capture('02b-visitors-with-data', async () => {
  const link = page.locator('a:has-text("Visitors")').first();
  await link.click({ timeout: 5000 });
});

await browser.close();

if (consoleErrors.length > 0) {
  console.log(`\n${consoleErrors.length} console errors:`);
  consoleErrors.slice(0, 5).forEach(e => console.log(`  ${e}`));
}

console.log('\nScreenshots saved to /tmp/sb-local-*.png');
