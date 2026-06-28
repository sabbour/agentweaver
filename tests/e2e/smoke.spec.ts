import { test, expect, request } from '@playwright/test';

const BASE = 'https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io';
// Token passed via env; CI sets GH_TOKEN; locally populated from `gh auth token`
const GH_TOKEN = process.env.GH_TOKEN ?? '';

// ---------------------------------------------------------------------------
// 1. Homepage loads
// ---------------------------------------------------------------------------
test('homepage loads and returns React SPA shell', async ({ page }) => {
  const response = await page.goto('/');
  expect(response?.status()).toBe(200);

  // React SPA injects a root div — the raw HTML should have id="root" or a script bundle
  const html = await page.content();
  expect(html).toMatch(/id="root"|<script[^>]+src=[^>]+\.js/i);
});

// ---------------------------------------------------------------------------
// 2. Sign-in page renders
// ---------------------------------------------------------------------------
test('sign-in page shows "Sign in with GitHub" button', async ({ page }) => {
  await page.goto('/sign-in');
  // Wait for React to hydrate and render the button
  await page.waitForLoadState('networkidle');

  // SignInPage renders: <button ...>Sign in with GitHub</button>
  const btn = page.locator('button', { hasText: /sign in with github/i });
  await expect(btn).toBeVisible({ timeout: 15_000 });
});

// ---------------------------------------------------------------------------
// 3. OAuth redirect — clicking sign-in navigates to /auth/github/authorize
// ---------------------------------------------------------------------------
test('sign-in button redirects to GitHub OAuth authorize endpoint', async ({ page }) => {
  await page.goto('/sign-in');
  await page.waitForLoadState('networkidle');

  // Capture where the navigation goes without following it
  let capturedUrl = '';
  await page.route('**/auth/github/authorize**', (route) => {
    capturedUrl = route.request().url();
    route.abort('aborted'); // don't actually navigate away
  });

  const btn = page.locator('button', { hasText: /sign in with github/i });
  await expect(btn).toBeVisible({ timeout: 15_000 });

  // Click — the onClick sets window.location.href = '/auth/github/authorize'
  await Promise.race([
    btn.click(),
    page.waitForNavigation({ timeout: 5_000 }).catch(() => {}),
  ]);

  // Give route handler a moment to fire
  await page.waitForTimeout(500);

  expect(
    capturedUrl || page.url(),
    'Expected navigation toward /auth/github/authorize',
  ).toMatch(/auth\/github\/authorize/i);
});

// ---------------------------------------------------------------------------
// 4. API health check
// ---------------------------------------------------------------------------
test('GET /api/health returns {"status":"ok"}', async () => {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });
  const resp = await ctx.get(`${BASE}/api/health`);
  expect(resp.status()).toBe(200);
  const body = await resp.json();
  expect(body).toMatchObject({ status: 'ok' });
  await ctx.dispose();
});

// ---------------------------------------------------------------------------
// 5. Docs site
// ---------------------------------------------------------------------------
test('GET /docs returns 200 with VitePress/Agentweaver content', async () => {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });

  for (const path of ['/docs', '/docs/']) {
    const resp = await ctx.get(`${BASE}${path}`);
    expect(resp.status(), `Expected 200 for ${path}`).toBe(200);
    const text = await resp.text();
    // VitePress generates an HTML page with a <title> and typically mentions the project
    expect(text).toMatch(/<title>|agentweaver/i);
  }

  await ctx.dispose();
});

// ---------------------------------------------------------------------------
// 6. Auth enforcement — unauthenticated request should return 401
// ---------------------------------------------------------------------------
test('GET /api/projects without auth returns 401', async () => {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });
  const resp = await ctx.get(`${BASE}/api/projects`);
  expect(resp.status()).toBe(401);
  await ctx.dispose();
});

// ---------------------------------------------------------------------------
// 7. Authenticated API — GitHub token should return signed-in status
// ---------------------------------------------------------------------------
test('GET /api/auth/github with valid token returns signed-in status', async () => {
  if (!GH_TOKEN) {
    test.skip(true, 'GH_TOKEN not set — skipping authenticated test');
  }

  const ctx = await request.newContext({
    ignoreHTTPSErrors: true,
    extraHTTPHeaders: { Authorization: `Bearer ${GH_TOKEN}` },
  });

  const resp = await ctx.get(`${BASE}/api/auth/github`);
  expect(resp.status()).toBe(200);

  const body = await resp.json() as { status: string; login?: string };
  // API returns {"status":"signed_in","login":"...","avatar_url":"..."}
  expect(body.status).toBe('signed_in');
  expect(typeof body.login).toBe('string');

  await ctx.dispose();
});
