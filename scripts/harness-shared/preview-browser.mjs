import { createHash } from 'node:crypto';
import { openBrowserSession } from '../ui-harness/lib/browser.mjs';

export async function verifyRenderedPreview(url, expectedText, { open = openBrowserSession, timeoutMs = 30_000 } = {}) {
  if (typeof expectedText !== 'string' || !expectedText.trim()) {
    throw new Error('A nonempty expected application text is required for preview acceptance.');
  }
  const session = await open({ baseUrl: url, headless: true });
  const errors = [];
  const page = session.page;
  page.on('pageerror', (error) => errors.push(`page: ${error.message}`));
  page.on('console', (entry) => {
    if (entry.type() === 'error') errors.push(`console: ${entry.text()}`);
  });
  page.on('requestfailed', (request) => errors.push(`network: ${request.failure()?.errorText ?? 'failed'}`));
  page.on('response', (response) => {
    if (response.status() >= 400 && response.request().resourceType() !== 'image') {
      errors.push(`HTTP ${response.status()} (${response.request().resourceType()})`);
    }
  });
  try {
    const response = await page.goto(url, { waitUntil: 'networkidle', timeout: timeoutMs });
    const status = response?.status() ?? 0;
    const text = await page.locator('body').innerText({ timeout: timeoutMs });
    const matched = text.includes(expectedText);
    const evidence = {
      ready: status === 200 && matched && errors.length === 0,
      status,
      expectedTextMatched: matched,
      bodySha256: createHash('sha256').update(text).digest('hex'),
      title: await page.title(),
      errors,
    };
    return evidence;
  } finally {
    await session.close();
  }
}
