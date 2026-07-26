import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';
import { loadStorageState, loadSessionStorageSeed } from './auth.mjs';

const GITHUB_OAUTH_ORIGIN = 'https://github.com';
const GITHUB_OAUTH_PATHS = new Set(['/login', '/session']);
const GENERATED_PREVIEW_LABEL = /^(?:[a-z]+-){3}[a-z2-7]{26}-preview$/;

function isAllowedGitHubOAuthNavigation(target, options) {
  return options.allowGitHubOAuthNavigation === true
    && target.origin === GITHUB_OAUTH_ORIGIN
    && (target.pathname.startsWith('/login/oauth/') || GITHUB_OAUTH_PATHS.has(target.pathname));
}

function isAllowedAgentweaverPreviewNavigation(base, target, options) {
  if (options.allowAgentweaverPreviewNavigation !== true || target.protocol !== 'https:') return false;
  assertTargetAllowed(target, options);

  const baseHost = base.hostname.toLowerCase().replace(/\.$/, '');
  if (!baseHost.startsWith('agentweaver.')) return false;

  const zone = baseHost.slice('agentweaver.'.length);
  const previewSuffixes = [`preview.${zone}`, zone];
  const previewSuffix = previewSuffixes.find((suffix) => target.hostname.endsWith(`.${suffix}`));
  const previewLabel = previewSuffix
    ? target.hostname.slice(0, target.hostname.length - previewSuffix.length - 1)
    : '';

  return GENERATED_PREVIEW_LABEL.test(previewLabel);
}

function guardedUrl(baseUrl, destination, options) {
  assertTargetAllowed(baseUrl, options);
  const base = new URL(baseUrl);
  const target = new URL(destination, base);
  if (target.origin !== base.origin && isAllowedGitHubOAuthNavigation(target, options)) return target;
  if (target.origin !== base.origin && isAllowedAgentweaverPreviewNavigation(base, target, options)) return target;
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
  if (opts.storageState) {
    const seed = await loadSessionStorageSeed(opts.storageState);
    if (seed && seed.origin === base.origin) {
      // Re-hydrate sessionStorage before any page script runs, since Agentweaver's
      // auth token lives there and storageState() cannot capture it (see auth.mjs).
      await context.addInitScript((entries) => {
        for (const [key, value] of Object.entries(entries)) {
          try { window.sessionStorage.setItem(key, value); } catch { /* storage unavailable */ }
        }
      }, seed.entries);
    }
  }
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
    gotoPreview: (destination) => page.goto(guardedUrl(base, destination, {
      ...opts,
      allowAgentweaverPreviewNavigation: true,
    }).toString(), { waitUntil: 'domcontentloaded' }),
    close: async () => { await context.close(); await browser.close(); },
  };
}

export function keyedLocator(page, target) {
  if (target?.testId) return page.getByTestId(target.testId);
  if (target?.role && target?.name) return page.getByRole(target.role, { name: target.name, exact: true });
  throw new Error('a UI target must specify data-testid or an exact ARIA role and accessible name');
}

export { guardedUrl };
