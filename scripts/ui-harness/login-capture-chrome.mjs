/**
 * Retired: generic Chrome automation cannot satisfy the managed Default-profile
 * authentication contract and must not be used as a fallback.
 */
throw new Error(
  'login-capture-chrome.mjs is retired. Close Chrome and run '
  + 'node scripts/ui-harness/login-chrome-default.mjs --base-url <staging-url>. '
  + 'Do not use generic Playwright, CDP/DevTools, or ad-hoc browser profile automation.',
);
