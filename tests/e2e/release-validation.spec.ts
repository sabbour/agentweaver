import { test, expect, request } from '@playwright/test';

const BASE =
  process.env.AKS_BASE_URL ??
  'https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io';
const GH_TOKEN = process.env.GH_TOKEN ?? '';

type ProjectSummary = { id?: string; projectId?: string; name?: string };

function requireGitHubToken() {
  if (!GH_TOKEN) {
    test.skip(true, 'GH_TOKEN not set — skipping authenticated AKS release validation');
  }
}

async function authenticatedContext() {
  requireGitHubToken();
  return request.newContext({
    ignoreHTTPSErrors: true,
    extraHTTPHeaders: { Authorization: `Bearer ${GH_TOKEN}` },
  });
}

test('sign-in page offers GitHub auth and routes to the authorize flow', async ({ page }) => {
  await page.goto('/sign-in');
  await page.waitForLoadState('networkidle');

  let capturedUrl = '';
  await page.route('**/auth/github/authorize**', (route) => {
    capturedUrl = route.request().url();
    route.abort('aborted');
  });

  const button = page.locator('button', { hasText: /sign in with github/i });
  await expect(button).toBeVisible({ timeout: 15_000 });
  await Promise.race([
    button.click(),
    page.waitForNavigation({ timeout: 5_000 }).catch(() => {}),
  ]);

  await page.waitForTimeout(500);
  expect(capturedUrl || page.url()).toMatch(/auth\/github\/authorize/i);
});

test('project APIs reject unauthenticated access', async () => {
  const ctx = await request.newContext({ ignoreHTTPSErrors: true });
  const resp = await ctx.get(`${BASE}/api/projects`);
  expect(resp.status()).toBe(401);
  await ctx.dispose();
});

test('authenticated GitHub token resolves signed-in status', async () => {
  const ctx = await authenticatedContext();
  const resp = await ctx.get(`${BASE}/api/auth/github`);
  expect(resp.status()).toBe(200);

  const body = (await resp.json()) as { status: string; login?: string };
  expect(body.status).toBe('signed_in');
  expect(typeof body.login).toBe('string');

  await ctx.dispose();
});

test('authenticated project memory and decision inbox surfaces are reachable', async () => {
  const ctx = await authenticatedContext();

  const projectsResp = await ctx.get(`${BASE}/api/projects`);
  expect(projectsResp.status()).toBe(200);

  const projectsBody = await projectsResp.json();
  const projects: ProjectSummary[] = Array.isArray(projectsBody)
    ? projectsBody
    : projectsBody.projects ?? projectsBody.items ?? [];

  if (projects.length === 0) {
    test.skip(true, 'Authenticated user has no projects to validate memory/decision surfaces');
  }

  const projectId = projects[0].id ?? projects[0].projectId;
  expect(projectId, 'Expected project id from /api/projects').toBeTruthy();

  for (const path of [
    `/api/projects/${encodeURIComponent(projectId!)}/memory`,
    `/api/projects/${encodeURIComponent(projectId!)}/decisions/inbox`,
    `/api/projects/${encodeURIComponent(projectId!)}/decisions`,
  ]) {
    const resp = await ctx.get(`${BASE}${path}`);
    expect(resp.status(), `${path} should be reachable for an authenticated project`).toBe(200);
    expect(resp.headers()['content-type']).toMatch(/application\/json/i);
  }

  await ctx.dispose();
});
