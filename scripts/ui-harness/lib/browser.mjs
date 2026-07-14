import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';
import { loadStorageState } from './auth.mjs';

function guardedUrl(baseUrl, destination, options) {
  assertTargetAllowed(baseUrl, options);
  const base = new URL(baseUrl);
  const target = new URL(destination, base);
  assertTargetAllowed(target, options);
  if (target.origin !== base.origin) throw new Error(`refusing cross-origin browser navigation from ${base.origin} to ${target.origin}`);
  return target;
}

async function playwrightChromium() {
  const { chromium } = await import('@playwright/test');
  return chromium;
}

/** Construct the browser boundary only after the shared target guard approves it. */
export async function openBrowserSession(opts) {
  const base = guardedUrl(opts.baseUrl, '/', opts);
  const chromium = await playwrightChromium();
  const browser = await chromium.launch({ headless: opts.headless !== false });
  const contextOptions = {};
  if (opts.storageState) contextOptions.storageState = await loadStorageState(opts.storageState);
  const context = await browser.newContext(contextOptions);
  const page = await context.newPage();
  await context.route('**/*', async (route) => {
    if (route.request().isNavigationRequest()) {
      try {
        guardedUrl(base, route.request().url(), opts);
      } catch {
        await route.abort('blockedbyclient');
        return;
      }
    }
    await route.continue();
  });
  return {
    baseUrl: base.toString(),
    browser, context, page,
    goto: (destination = '/') => page.goto(guardedUrl(base, destination, opts).toString(), { waitUntil: 'domcontentloaded' }),
    close: async () => { await context.close(); await browser.close(); },
  };
}

export function keyedLocator(page, target) {
  if (target?.testId) return page.getByTestId(target.testId);
  if (target?.role && target?.name) return page.getByRole(target.role, { name: target.name, exact: true });
  throw new Error('a UI target must specify data-testid or an exact ARIA role and accessible name');
}

export { guardedUrl };
