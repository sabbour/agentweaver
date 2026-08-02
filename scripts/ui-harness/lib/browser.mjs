import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';
import { loadStorageState, loadSessionStorageSeed } from './auth.mjs';

const DEFAULT_IDENTITY_PROVIDER_ORIGINS = Object.freeze([
  'https://github.com',
  'https://login.microsoftonline.com',
  'https://login.live.com',
]);
const GENERATED_PREVIEW_LABEL = /^(?:[a-z]+-){3}[a-z2-7]{26}-preview$/;

function identityProviderOrigins(options = {}) {
  const configured = Array.isArray(options.identityProviderOrigins)
    ? options.identityProviderOrigins
    : [];
  const origins = new Set(DEFAULT_IDENTITY_PROVIDER_ORIGINS);
  for (const candidate of configured) {
    try {
      origins.add(new URL(candidate).origin);
    } catch {
      // Ignore malformed candidates from optional config probing.
    }
  }
  return origins;
}

// The manual `login` subcommand in tools.mjs is the ONLY caller that ever sets
// allowIdentityProviderNavigation, and it only does so for its own
// human-supervised, headful browser session. A real person can be routed
// through many identity-provider paths mid-login beyond the initial authorize
// and callback hops -- GitHub 2FA / device verification / org SSO / WebAuthn,
// or Entra / Microsoft account MFA and conditional-access detours -- so we
// allow the whole configured IdP origins here rather than chase an incomplete
// path allowlist. This is safe specifically because it never applies to the
// automated/persona-driven action() codepath, which does not (and must not)
// set this flag, so headless scripted flows remain fully restricted to
// same-origin navigation.
function isAllowedIdentityProviderNavigation(target, options = {}) {
  return options.allowIdentityProviderNavigation === true
    && identityProviderOrigins(options).has(target.origin);
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
  if (target.origin !== base.origin && isAllowedIdentityProviderNavigation(target, options)) return target;
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
  const launchOptions = { headless: opts.headless !== false };
  if (opts.browserChannel) launchOptions.channel = opts.browserChannel;
  if (opts.browserArgs) launchOptions.args = opts.browserArgs;
  const contextOptions = {};
  if (opts.storageState) contextOptions.storageState = await loadStorageState(opts.storageState);
  let browser;
  let context;
  if (opts.userDataDir) {
    context = await chromium.launchPersistentContext(opts.userDataDir, launchOptions);
    browser = context.browser();
  } else {
    browser = await chromium.launch(launchOptions);
    context = await browser.newContext(contextOptions);
  }
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
    close: async () => {
      await context.close();
      await browser?.close().catch(() => {});
    },
  };
}

export function keyedLocator(page, target) {
  if (target?.testId) return page.getByTestId(target.testId);
  if (target?.role && target?.name) return page.getByRole(target.role, { name: target.name, exact: true });
  throw new Error('a UI target must specify data-testid or an exact ARIA role and accessible name');
}

export { guardedUrl };
