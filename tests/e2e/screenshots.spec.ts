import { test, type Page } from '@playwright/test';

/**
 * ============================================================================
 * DRAFT — User Guide (Web) screenshot capture spec
 * ============================================================================
 *
 * STATUS: DRAFT / SKIPPED. This spec is intentionally guarded so it NEVER runs
 * in CI. It exists to automate capture of the placeholder screenshots documented
 * in `docs/experience/screenshot-plan.md` once the AKS site is published.
 *
 * It is NOT wired into a Playwright project/config and is NOT meant to be
 * committed by the coordinator unless the user explicitly wants it.
 *
 * ----------------------------------------------------------------------------
 * HOW TO RUN (later, against the PUBLISHED AKS site — not localhost):
 * ----------------------------------------------------------------------------
 *   1. Sign in to the published site once in a real browser and export the
 *      authenticated context so Playwright can reuse the signed-in session:
 *
 *        npx playwright open --save-storage=auth.json https://<published-aks-host>
 *        # sign in with GitHub in the opened browser, then close it.
 *
 *   2. Provide the runtime config via env vars and run ONLY this spec:
 *
 *        $env:BASE_URL       = "https://<published-aks-host>"
 *        $env:STORAGE_STATE  = "auth.json"          # reuse signed-in cookies
 *        $env:PROJECT_ID     = "<an-existing-project-id>"
 *        $env:RUN_ID         = "<an-existing-run-id>"            # optional
 *        $env:EXECUTION_ID   = "<an-existing-execution-id>"      # optional
 *        # Optional sign-in fallback if STORAGE_STATE is not provided:
 *        $env:GITHUB_USERNAME = "..."
 *        $env:GITHUB_PASSWORD = "..."
 *
 *        npx playwright test tests/e2e/screenshots.spec.ts --headed
 *
 * Screenshots are written to `docs/public/screenshots/<name>.png`, the path
 * VitePress serves them from (referenced as `/screenshots/<name>.png`).
 *
 * ----------------------------------------------------------------------------
 * SAFETY GUARD: every test is skipped unless BASE_URL is set, so an accidental
 * `playwright test` run (e.g. in CI with no env) is a no-op.
 * ----------------------------------------------------------------------------
 */

const BASE_URL = process.env.BASE_URL ?? '';
const STORAGE_STATE = process.env.STORAGE_STATE ?? '';
const PROJECT_ID = process.env.PROJECT_ID ?? '';
const RUN_ID = process.env.RUN_ID ?? '';
const EXECUTION_ID = process.env.EXECUTION_ID ?? '';

const SHOT_DIR = 'docs/public/screenshots';
const shot = (name: string) => `${SHOT_DIR}/${name}.png`;

// Run sequentially so dialogs/modals do not collide across captures.
test.describe.configure({ mode: 'serial' });

// Reuse the signed-in browser context (storageState) when available.
if (STORAGE_STATE) {
  test.use({ storageState: STORAGE_STATE });
}

/**
 * Best-effort sign-in fallback. If storageState already carries a signed-in
 * session, the app shell renders and this returns immediately. Otherwise it
 * clicks "Sign in with GitHub" and attempts a GitHub username/password login
 * (only when GITHUB_USERNAME / GITHUB_PASSWORD are provided).
 */
async function ensureSignedIn(page: Page): Promise<void> {
  await page.goto(`${BASE_URL}/overview`, { waitUntil: 'domcontentloaded' });

  // Already signed in: the primary navigation rail is present.
  const nav = page.getByRole('navigation', { name: 'Primary navigation' });
  if (await nav.isVisible().catch(() => false)) {
    return;
  }

  // Sign-in page: the SignInPage renders a "Sign in with GitHub" button.
  const signInButton = page.getByRole('button', { name: 'Sign in with GitHub' });
  if (await signInButton.isVisible().catch(() => false)) {
    await signInButton.click();

    const ghUser = process.env.GITHUB_USERNAME;
    const ghPass = process.env.GITHUB_PASSWORD;
    if (ghUser && ghPass) {
      await page.waitForURL(/github\.com\/login/, { timeout: 30_000 }).catch(() => undefined);
      await page.fill('#login_field', ghUser).catch(() => undefined);
      await page.fill('#password', ghPass).catch(() => undefined);
      await page.click('input[name="commit"]').catch(() => undefined);
    }

    // Wait to be redirected back into the signed-in shell.
    await page.getByRole('navigation', { name: 'Primary navigation' }).waitFor({ timeout: 60_000 });
  }
}

test.beforeEach(async ({ page }) => {
  // Hard guard: never run unless explicitly pointed at the published AKS site.
  // This keeps the DRAFT spec a no-op in CI and local `playwright test` runs.
  test.skip(!BASE_URL, 'DRAFT: set BASE_URL to the published AKS site to capture screenshots.');
  await ensureSignedIn(page);
});

// Helper: navigate to a route and wait for a key element before capturing.
async function captureAt(
  page: Page,
  route: string,
  ready: () => Promise<void>,
  name: string,
): Promise<void> {
  await page.goto(`${BASE_URL}${route}`, { waitUntil: 'domcontentloaded' });
  await ready();
  await page.screenshot({ path: shot(name), fullPage: true });
}

// Project-scoped tests need a real PROJECT_ID; skip cleanly when absent.
const projectRoute = (suffix = '') => `/projects/${PROJECT_ID}${suffix}`;

// ============================================================================
// 00-overview.md
// ============================================================================
test.describe('User Guide · Overview', () => {
  test('app-shell.png', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByRole('navigation', { name: 'Primary navigation' }).waitFor();
    }, 'app-shell');
  });

  test('overview-fleet.png', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByText('Fleet activity at a glance.').waitFor();
    }, 'overview-fleet');
  });
});

// ============================================================================
// onboarding-auth.md  (unauthenticated states — capture WITHOUT storageState)
// ============================================================================
test.describe('User Guide · Onboarding & auth', () => {
  test('signin-page.png', async ({ browser }) => {
    // Fresh, signed-out context so the SignInPage renders.
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Sign in with GitHub' }).waitFor();
    await page.screenshot({ path: shot('signin-page') });
    await ctx.close();
  });

  test('signin-error.png', async ({ browser }) => {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.goto(`${BASE_URL}/?auth=error&reason=Authentication%20failed.`, {
      waitUntil: 'domcontentloaded',
    });
    await page.getByText('Authentication failed.').waitFor();
    await page.screenshot({ path: shot('signin-error') });
    await ctx.close();
  });

  test('signed-in-topbar.png', async ({ page }) => {
    await page.goto(`${BASE_URL}/overview`, { waitUntil: 'domcontentloaded' });
    // Open the GitHub account trigger in the top bar to reveal "Sign out".
    await page.getByText('Sign out').first().waitFor({ state: 'attached' }).catch(() => undefined);
    await page.screenshot({ path: shot('signed-in-topbar') });
  });
});

// ============================================================================
// projects.md
// ============================================================================
test.describe('User Guide · Projects', () => {
  test('projects-gallery.png', async ({ page }) => {
    await captureAt(page, '/projects', async () => {
      await page.getByText('Your Agentweaver projects.').waitFor();
    }, 'projects-gallery');
  });

  test('create-blank-project-dialog.png', async ({ page }) => {
    await page.goto(`${BASE_URL}/projects`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Create blank project' }).click();
    await page.getByRole('textbox', { name: 'Name' }).waitFor();
    await page.getByRole('textbox', { name: 'Name' }).fill('Demo project');
    await page.screenshot({ path: shot('create-blank-project-dialog') });
  });

  test('create-from-github-dialog.png', async ({ page }) => {
    await page.goto(`${BASE_URL}/projects`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Create from GitHub' }).click();
    await page.getByLabel('Organization').waitFor();
    await page.screenshot({ path: shot('create-from-github-dialog') });
  });

  test('project-dashboard.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute(), async () => {
      await page.getByText('Delivery metrics and the agent leaderboard.').waitFor();
    }, 'project-dashboard');
  });

  test('project-settings.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/settings'), async () => {
      await page.getByText('General').first().waitFor();
    }, 'project-settings');
  });
});

// ============================================================================
// runs-board-watch.md
// ============================================================================
test.describe('User Guide · Runs, board & watch', () => {
  test('project-board.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/board'), async () => {
      await page.getByText('Backlog, Ready, and in-flight work.').waitFor();
    }, 'project-board');
  });

  test('run-card-actions.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/board')}`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Runs').first().waitFor();
    await page.screenshot({ path: shot('run-card-actions'), fullPage: true });
  });

  test('workflow-run-graph.png', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture run screenshots.');
    await captureAt(page, projectRoute(`/runs/${RUN_ID}/workflow`), async () => {
      await page.getByRole('button', { name: 'Auto-approve tools' }).waitFor().catch(() => undefined);
    }, 'workflow-run-graph');
  });

  test('sandbox-preview-dialog.png', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires a Kubernetes sandbox on an active run.');
    await page.goto(`${BASE_URL}${projectRoute(`/runs/${RUN_ID}/workflow`)}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.getByRole('button', { name: 'Preview' }).click();
    await page.getByText('Sandbox Preview').waitFor();
    await page.screenshot({ path: shot('sandbox-preview-dialog') });
  });

  test('watch-timeline.png', async ({ page }) => {
    test.skip(
      !PROJECT_ID || !RUN_ID || !EXECUTION_ID,
      'Set PROJECT_ID, RUN_ID and EXECUTION_ID to capture the execution timeline.',
    );
    await captureAt(page, projectRoute(`/runs/${RUN_ID}/execution/${EXECUTION_ID}`), async () => {
      await page.getByRole('navigation', { name: 'Breadcrumb' }).waitFor().catch(() => undefined);
    }, 'watch-timeline');
  });
});

// ============================================================================
// review-workspace-merge.md
// ============================================================================
test.describe('User Guide · Review & workspace', () => {
  test('workspace-browser.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/workspace'), async () => {
      await page.getByText('Browse the project repository and active run worktrees, read-only.').waitFor();
    }, 'workspace-browser');
  });

  test('review-changes-tab.png', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires a run with artifacts to review.');
    await page.goto(`${BASE_URL}${projectRoute(`/runs/${RUN_ID}/workflow`)}`, {
      waitUntil: 'domcontentloaded',
    });
    // Open the run's artifact/review view, then the Changes tab.
    await page.getByRole('tab', { name: 'Changes' }).click().catch(() => undefined);
    await page.getByText('Branch Changes').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('review-changes-tab') });
  });

  test('review-file-viewer.png', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires a changed file to open in the viewer.');
    await page.goto(`${BASE_URL}${projectRoute(`/runs/${RUN_ID}/workflow`)}`, {
      waitUntil: 'domcontentloaded',
    });
    await page.getByRole('tab', { name: 'Changes' }).click().catch(() => undefined);
    // Open the first changed-file row to launch the file viewer modal.
    await page.getByRole('row').nth(1).click().catch(() => undefined);
    await page.getByRole('button', { name: 'Close' }).waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('review-file-viewer') });
  });
});

// ============================================================================
// team-casting-memory.md
// ============================================================================
test.describe('User Guide · Team, casting & memory', () => {
  test('team-roster.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/team'), async () => {
      await page.getByText('The cast working on this project.').waitFor();
    }, 'team-roster');
  });

  test('team-member-detail.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires at least one roster member.');
    await page.goto(`${BASE_URL}${projectRoute('/team')}`, { waitUntil: 'domcontentloaded' });
    // Open the first member's detail drawer.
    await page.getByRole('button', { name: /^Open details for / }).first().click().catch(() => undefined);
    await page.getByRole('tab', { name: 'Charter' }).waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('team-member-detail') });
  });

  test('casting-wizard-cast.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/team/cast'), async () => {
      await page.getByText('Cast a team').first().waitFor();
    }, 'casting-wizard-cast');
  });

  test('casting-wizard-review.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires generating a proposal to reach the Review step.');
    await page.goto(`${BASE_URL}${projectRoute('/team/cast')}`, { waitUntil: 'domcontentloaded' });
    // NOTE: reaching "Review proposal" requires a generated proposal (Formulate/Analyze
    // → Review). Drive those steps here before capturing, or pre-seed a proposal.
    await page.getByText('Review proposal').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('casting-wizard-review') });
  });

  test('memories-decisions.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/memories'), async () => {
      await page.getByText('Team Memory').first().waitFor();
    }, 'memories-decisions');
  });

  test('memories-agent-memory.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/memories')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('tab', { name: 'Agent Memory' }).click().catch(() => undefined);
    await page.getByLabel('Create memory entry').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('memories-agent-memory'), fullPage: true });
  });
});

// ============================================================================
// workflows-backlog.md
// ============================================================================
test.describe('User Guide · Workflows & backlog', () => {
  test('workflows-list.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/workflows'), async () => {
      await page.getByText('Reusable pipeline definitions.').waitFor();
    }, 'workflows-list');
  });

  test('per-run-workflow-graph.png', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture the run graph.');
    await captureAt(page, projectRoute(`/runs/${RUN_ID}/workflow`), async () => {
      await page.getByText('Run').first().waitFor();
    }, 'per-run-workflow-graph');
  });

  test('backlog-ready.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/board')}`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Capture a task into Backlog').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('backlog-ready'), fullPage: true });
  });

  test('decompose-preview-dialog.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires a Markdown spec file in the workspace.');
    await page.goto(`${BASE_URL}${projectRoute('/workspace')}`, { waitUntil: 'domcontentloaded' });
    // Select a Markdown spec file, then Import to backlog → preview.
    await page.getByRole('button', { name: 'Import to backlog' }).click().catch(() => undefined);
    await page.getByText('Preview proposed backlog items').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('decompose-preview-dialog') });
  });
});

// ============================================================================
// operations.md
// ============================================================================
test.describe('User Guide · Operations', () => {
  test('diagnostics-checks.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/diagnostics'), async () => {
      await page.getByText('System and project health checks.').waitFor();
    }, 'diagnostics-checks');
  });

  test('heartbeat-status.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/heartbeat'), async () => {
      await page.getByText('Background automation status and recent ticks.').waitFor();
    }, 'heartbeat-status');
  });

  test('flow-agents.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/flow'), async () => {
      await page.getByText('What each agent is working on right now.').waitFor();
    }, 'flow-agents');
  });

  test('sandbox-policy.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    // Sandbox policy lives under Settings; deep-link to that section.
    await page.goto(`${BASE_URL}${projectRoute('/settings')}`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Sandbox policy').first().click().catch(() => undefined);
    await page.getByText('Shell execution').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('sandbox-policy'), fullPage: true });
  });
});

// ============================================================================
// scaling-operations.md
// ============================================================================
test.describe('User Guide · Scaling operations', () => {
  test('overview-active-projects.png', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByText('Active projects').first().waitFor();
    }, 'overview-active-projects');
  });

  test('diagnostics-global-health.png', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to reach the Diagnostics page.');
    await page.goto(`${BASE_URL}${projectRoute('/diagnostics')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('tab', { name: 'Global' }).click().catch(() => undefined);
    await page.getByText('API version').first().waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('diagnostics-global-health'), fullPage: true });
  });
});
