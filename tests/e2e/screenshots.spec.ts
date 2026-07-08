import { test, type Page } from '@playwright/test';

/**
 * DRAFT — User Guide (Web) screenshot capture spec.
 *
 * Guarded by BASE_URL so it is skipped in CI/local accidental runs. Captures the
 * screenshots listed in docs/experience/screenshot-plan.md against a published
 * site, optionally reusing STORAGE_STATE and PROJECT_ID/RUN_ID fixtures.
 */

const BASE_URL = process.env.BASE_URL ?? '';
const STORAGE_STATE = process.env.STORAGE_STATE ?? '';
const PROJECT_ID = process.env.PROJECT_ID ?? '';
const RUN_ID = process.env.RUN_ID ?? '';
const EXECUTION_ID = process.env.EXECUTION_ID ?? '';

const SHOT_DIR = 'docs/public/screenshots';
const shot = (name: string) => `${SHOT_DIR}/${name}.png`;

test.describe.configure({ mode: 'serial' });

if (STORAGE_STATE) {
  test.use({ storageState: STORAGE_STATE });
}

async function ensureSignedIn(page: Page): Promise<void> {
  await page.goto(`${BASE_URL}/overview`, { waitUntil: 'domcontentloaded' });

  const nav = page.getByRole('navigation', { name: 'Primary navigation' });
  if (await nav.isVisible().catch(() => false)) return;

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

    await page.getByRole('navigation', { name: 'Primary navigation' }).waitFor({ timeout: 60_000 });
  }
}

test.beforeEach(async ({ page }) => {
  test.skip(!BASE_URL, 'DRAFT: set BASE_URL to the published AKS site to capture screenshots.');
  await ensureSignedIn(page);
});

async function captureAt(page: Page, route: string, ready: () => Promise<void>, path: string): Promise<void> {
  await page.goto(`${BASE_URL}${route}`, { waitUntil: 'domcontentloaded' });
  await ready();
  await page.screenshot({ path, fullPage: true });
}

const projectRoute = (suffix = '') => `/projects/${PROJECT_ID}${suffix}`;

async function openRunControls(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Show controls' }).click().catch(() => undefined);
}

async function openArtifacts(page: Page): Promise<void> {
  await openRunControls(page);
  await page.getByRole('button', { name: 'Artifacts' }).click().catch(() => undefined);
}

// Overview and auth
test.describe('User Guide · Overview and auth', () => {
  test('app-shell', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByRole('navigation', { name: 'Primary navigation' }).waitFor();
      await page.getByRole('button', { name: 'Open control console' }).waitFor();
    }, shot('app-shell'));
  });

  test('overview-fleet', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByText('A live command center for projects').waitFor();
      await page.getByText('Recent Projects').waitFor().catch(() => undefined);
    }, shot('overview-fleet'));
  });

  test('overview-active-projects', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByText('Active projects').first().waitFor();
      await page.getByText('Recent Projects').waitFor().catch(() => undefined);
    }, shot('overview-active-projects'));
  });

  test('overview-token-usage', async ({ page }) => {
    await captureAt(page, '/overview', async () => {
      await page.getByText('AI Usage & Performance').waitFor();
    }, shot('overview-token-usage'));
  });

  test('signin-page', async ({ browser }) => {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Sign in with GitHub' }).waitFor();
    await page.screenshot({ path: shot('signin-page'), fullPage: true });
    await ctx.close();
  });

  test('signin-error', async ({ browser }) => {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.goto(`${BASE_URL}/?auth=error&reason=Authentication%20failed.`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Authentication failed.').waitFor();
    await page.screenshot({ path: shot('signin-error'), fullPage: true });
    await ctx.close();
  });

  test('signed-in-topbar', async ({ page }) => {
    await page.goto(`${BASE_URL}/overview`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Open control console' }).waitFor();
    await page.locator('button').filter({ hasText: /^(?!Console$).+/ }).last().click().catch(() => undefined);
    await page.getByText('Sign out').waitFor({ timeout: 5000 }).catch(() => undefined);
    await page.screenshot({ path: shot('signed-in-topbar'), fullPage: true });
  });

  test('browser-console', async ({ page }) => {
    await page.goto(`${BASE_URL}/console`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Agentweaver Console').waitFor();
    await page.screenshot({ path: shot('browser-console'), fullPage: true });
  });
});

// Projects and settings
test.describe('User Guide · Projects and settings', () => {
  test('projects-gallery', async ({ page }) => {
    await captureAt(page, '/projects', async () => {
      await page.getByText('Open an existing project, or create one from GitHub or a blueprint.').waitFor();
    }, shot('projects-gallery'));
  });

  test('create-blank-project-dialog', async ({ page }) => {
    await page.goto(`${BASE_URL}/projects`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Create blank project' }).click();
    await page.getByRole('dialog', { name: 'Create blank project' }).waitFor();
    await page.getByRole('textbox', { name: 'Project name *' }).fill('Demo project').catch(() => undefined);
    await page.screenshot({ path: shot('create-blank-project-dialog'), fullPage: true });
  });

  test('create-from-github-dialog', async ({ page }) => {
    await page.goto(`${BASE_URL}/projects`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Create from GitHub' }).click();
    await page.getByRole('dialog', { name: 'Create project from GitHub' }).waitFor();
    await page.screenshot({ path: shot('create-from-github-dialog'), fullPage: true });
  });

  test('repo-blueprint-suggestions', async ({ page }) => {
    await page.goto(`${BASE_URL}/projects`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Create from GitHub' }).click();
    await page.getByRole('combobox', { name: 'Repository' }).fill('sabbour/agentweaver').catch(() => undefined);
    await page.getByText('Suggested').first().waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('repo-blueprint-suggestions'), fullPage: true });
  });

  test('project-dashboard', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute(), async () => {
      await page.getByText('Project command center for live work').waitFor();
    }, shot('project-dashboard'));
  });

  test('dashboard-token-usage', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute(), async () => {
      await page.getByText('Operational signals').waitFor();
      await page.getByText('Agent leaderboard').waitFor().catch(() => undefined);
    }, shot('dashboard-token-usage'));
  });

  test('project-settings', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/settings'), async () => {
      await page.getByRole('navigation', { name: 'Settings sections' }).waitFor();
      await page.getByText('Generation models').waitFor().catch(() => undefined);
    }, shot('project-settings'));
  });

  test('project-generation-model-settings', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/settings?section=general'), async () => {
      await page.getByText('Generation models').scrollIntoViewIfNeeded();
    }, shot('project-generation-model-settings'));
  });

  test('sandbox-policy', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/settings?section=sandbox'), async () => {
      await page.getByText('Shell execution').waitFor();
    }, shot('sandbox-policy'));
  });
});

// Board, orchestrations, review, workspace
test.describe('User Guide · Runs, board, review, workspace', () => {
  test('project-board', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/board'), async () => {
      await page.getByText('Orchestrate agent tasks from intake').waitFor();
    }, shot('project-board'));
  });

  test('backlog-ready', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/board'), async () => {
      await page.getByText('Capture a task into Backlog').waitFor().catch(() => undefined);
      await page.getByText('Ready').first().waitFor();
    }, shot('backlog-ready'));
  });

  test('run-card-actions', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/board')}`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Run audit trail').waitFor().catch(() => undefined);
    await page.getByText(/runs$/i).first().waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('run-card-actions'), fullPage: true });
  });

  test('orchestrations-list', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/orchestrations'), async () => {
      await page.getByText('Coordinator runs across this project.').waitFor();
    }, shot('orchestrations-list'));
  });

  test('workflow-run-graph', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture run screenshots.');
    await captureAt(page, projectRoute(`/orchestrations/${RUN_ID}`), async () => {
      await page.getByTestId('run-operator-console').waitFor();
    }, shot('workflow-run-graph'));
  });

  test('sandbox-preview-dialog', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires PROJECT_ID, RUN_ID, and a Kubernetes sandbox run.');
    await page.goto(`${BASE_URL}${projectRoute(`/orchestrations/${RUN_ID}`)}`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('run-operator-console').waitFor();
    await openRunControls(page);
    await page.getByRole('button', { name: 'Preview Sandbox' }).click();
    await page.getByRole('dialog', { name: 'Sandbox Preview' }).waitFor();
    await page.screenshot({ path: shot('sandbox-preview-dialog'), fullPage: true });
  });

  test('watch-timeline', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture run screenshots.');
    await captureAt(page, projectRoute(`/orchestrations/${RUN_ID}`), async () => {
      await page.getByLabel('Selected task details').waitFor();
      if (EXECUTION_ID) {
        await page.getByText(EXECUTION_ID.slice(0, 8)).first().waitFor({ timeout: 3000 }).catch(() => undefined);
      }
    }, shot('watch-timeline'));
  });

  test('watch-token-counter', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture run screenshots.');
    await page.goto(`${BASE_URL}${projectRoute(`/orchestrations/${RUN_ID}`)}`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('run-operator-console').waitFor();
    await page.getByRole('button', { name: /AI credits/ }).click().catch(() => undefined);
    await page.getByText('Agent token breakdown').waitFor({ timeout: 5000 }).catch(() => undefined);
    await page.screenshot({ path: shot('watch-token-counter'), fullPage: true });
  });

  test('run-pending-capacity', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID to capture this screenshot.');
    await captureAt(page, projectRoute(`/orchestrations/${RUN_ID}`), async () => {
      await page.getByText('Waiting for capacity').first().waitFor().catch(() => undefined);
      await page.getByTestId('run-operator-console').waitFor();
    }, shot('run-pending-capacity'));
  });

  test('coordinator-topology-pod-chips', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Set PROJECT_ID and RUN_ID for a Kubernetes-backed coordinator run.');
    await captureAt(page, projectRoute(`/orchestrations/${RUN_ID}`), async () => {
      await openRunControls(page);
      await page.getByRole('button', { name: 'Topology' }).click().catch(() => undefined);
      await page.locator('[role="status"][aria-label^="Executing in pod"]').first().waitFor({ timeout: 10000 }).catch(() => undefined);
    }, shot('coordinator-topology-pod-chips'));
  });

  test('review-changes-tab', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires a run with artifacts to review.');
    await page.goto(`${BASE_URL}${projectRoute(`/orchestrations/${RUN_ID}`)}`, { waitUntil: 'domcontentloaded' });
    await openArtifacts(page);
    await page.getByRole('tab', { name: 'Changes' }).click().catch(() => undefined);
    await page.getByText('Branch Changes').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('review-changes-tab'), fullPage: true });
  });

  test('review-file-viewer', async ({ page }) => {
    test.skip(!PROJECT_ID || !RUN_ID, 'Requires a changed file to open in the viewer.');
    await page.goto(`${BASE_URL}${projectRoute(`/orchestrations/${RUN_ID}`)}`, { waitUntil: 'domcontentloaded' });
    await openArtifacts(page);
    await page.getByRole('tab', { name: 'Changes' }).click().catch(() => undefined);
    await page.getByRole('row').nth(1).click().catch(() => undefined);
    await page.getByRole('button', { name: 'Close' }).waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('review-file-viewer'), fullPage: true });
  });

  test('workspace-browser', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/workspace'), async () => {
      await page.getByText('Browse the project repository and active run worktrees, read-only.').waitFor();
    }, shot('workspace-browser'));
  });

  test('decompose-preview-dialog', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires a Markdown file in the workspace.');
    await page.goto(`${BASE_URL}${projectRoute('/workspace')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Import to backlog' }).click().catch(() => undefined);
    await page.getByText('Preview proposed backlog items').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('decompose-preview-dialog'), fullPage: true });
  });
});

// Team, casting, memory, skills
test.describe('User Guide · Team, casting, memory, skills', () => {
  test('team-roster', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/team'), async () => {
      await page.getByText('The cast working on this project.').waitFor();
    }, shot('team-roster'));
  });

  test('team-member-detail', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires at least one roster member.');
    await page.goto(`${BASE_URL}${projectRoute('/team')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: /^Open details for / }).first().click().catch(() => undefined);
    await page.getByRole('tab', { name: 'Charter' }).waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('team-member-detail'), fullPage: true });
  });

  test('casting-wizard-cast', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/team/cast'), async () => {
      await page.getByText('Cast a team').first().waitFor();
    }, shot('casting-wizard-cast'));
  });

  test('casting-wizard-review', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires a generated or selected proposal.');
    await page.goto(`${BASE_URL}${projectRoute('/team/cast')}`, { waitUntil: 'domcontentloaded' });
    await page.getByText('Review proposal').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('casting-wizard-review'), fullPage: true });
  });

  test('memories-decisions', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/memories'), async () => {
      await page.getByText('Team Memory').first().waitFor();
    }, shot('memories-decisions'));
  });

  test('memories-agent-memory', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/memories')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('tab', { name: 'Agent Memory' }).click();
    await page.getByLabel('Create memory entry').waitFor();
    await page.screenshot({ path: shot('memories-agent-memory'), fullPage: true });
  });

  test('skills-catalog', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/skills'), async () => {
      await page.getByText('Import, sync, and assign reusable agent skills').waitFor();
    }, shot('skills-catalog'));
  });

  test('skill-import-dialog', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/skills')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Import Skill' }).click();
    await page.getByRole('dialog', { name: 'Import Skill' }).waitFor();
    await page.screenshot({ path: shot('skill-import-dialog'), fullPage: true });
  });
});

// Workflows, operations, observability
test.describe('User Guide · Workflows, operations, observability', () => {
  test('workflows-list', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/workflows'), async () => {
      await page.getByText('Reusable pipeline definitions.').waitFor();
    }, shot('workflows-list'));
  });

  test('workflow-definition-graph', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/workflows')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: /View graph/ }).first().click().catch(() => undefined);
    await page.getByText('Workflow').first().waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('workflow-definition-graph'), fullPage: true });
  });

  test('flow-agents', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/flow'), async () => {
      await page.getByText('What each agent is working on right now.').waitFor();
    }, shot('flow-agents'));
  });

  test('diagnostics-checks', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/diagnostics'), async () => {
      await page.getByText('System and project health checks.').waitFor();
    }, shot('diagnostics-checks'));
  });

  test('diagnostics-global-health', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await page.goto(`${BASE_URL}${projectRoute('/diagnostics')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('tab', { name: 'Global' }).click().catch(() => undefined);
    await page.getByText('API version').first().waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('diagnostics-global-health'), fullPage: true });
  });

  test('heartbeat-status', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/heartbeat'), async () => {
      await page.getByText('Background automation status and recent ticks.').waitFor();
    }, shot('heartbeat-status'));
  });

  test('heartbeat-automation-column', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/heartbeat'), async () => {
      await page.getByLabel('Recent heartbeat ticks').waitFor().catch(() => undefined);
      await page.getByText('Automation').first().waitFor().catch(() => undefined);
    }, shot('heartbeat-automation-column'));
  });

  test('cluster-page', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/cluster'), async () => {
      await page.getByText('Kubernetes cluster health and capacity.').waitFor();
    }, shot('cluster-page'));
  });

  test('observability-overview', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/observability'), async () => {
      await page.getByText('Model performance, token usage, and invocation trends.').waitFor();
    }, shot('observability-overview'));
  });

  test('observability-agents', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/observability/agents'), async () => {
      await page.getByText('Cross-run token usage aggregated by agent.').waitFor();
    }, shot('observability-agents'));
  });

  test('observability-traces', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Set PROJECT_ID to capture project-scoped screenshots.');
    await captureAt(page, projectRoute('/observability/traces'), async () => {
      await page.getByText('Recent coordinator traces with links back to the live run view.').waitFor();
    }, shot('observability-traces'));
  });

  test('observability-trace-preview', async ({ page }) => {
    test.skip(!PROJECT_ID, 'Requires a coordinator run with trace data.');
    await page.goto(`${BASE_URL}${projectRoute('/observability/traces')}`, { waitUntil: 'domcontentloaded' });
    await page.getByRole('button', { name: 'Preview trace' }).first().click().catch(() => undefined);
    await page.getByText('Recent trace preview').waitFor().catch(() => undefined);
    await page.screenshot({ path: shot('observability-trace-preview'), fullPage: true });
  });
});
