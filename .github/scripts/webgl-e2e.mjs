#!/usr/bin/env node
// Headless Chromium (Playwright) runner for Unity WebGL E2E smoke tests.

import { chromium } from 'playwright';

const url = process.argv[2] || 'http://127.0.0.1:8080/';
const timeoutMs = parseInt(process.argv[3] || '600000', 10);

async function main() {
  const browser = await chromium.launch({
    headless: true,
    args: [
      '--use-gl=angle',
      '--use-angle=swiftshader',
      '--enable-unsafe-swiftshader',
      '--enable-webgl',
      '--ignore-gpu-blocklist',
      '--no-sandbox',
    ],
  });

  const context = await browser.newContext();
  const page = await context.newPage();

  let passed = false;
  let failedReason = null;

  page.on('console', (msg) => {
    const text = msg.text();
    console.log(`[browser:${msg.type()}] ${text}`);
    if (text.includes('ASTC_E2E_PASSED')) {
      passed = true;
    } else if (text.includes('ASTC_E2E_FAILED')) {
      failedReason = text;
    }
  });

  page.on('pageerror', (err) => {
    console.error(`[browser:pageerror] ${err.message}`);
    failedReason = failedReason || err.message;
  });

  console.log(`[webgl-e2e] Navigating to ${url} (timeout=${timeoutMs}ms)...`);
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });

  const deadline = Date.now() + timeoutMs;
  while (!passed && !failedReason && Date.now() < deadline) {
    await page.waitForTimeout(500);
  }

  await browser.close();

  if (passed && !failedReason) {
    console.log('[webgl-e2e] SUCCESS: ASTC_E2E_PASSED observed.');
    process.exit(0);
  }

  console.error(`[webgl-e2e] FAILED: ${failedReason || 'Timed out waiting for ASTC_E2E_PASSED'}`);
  process.exit(1);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
