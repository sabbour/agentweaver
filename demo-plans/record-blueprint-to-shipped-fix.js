async page => {
  const STAGING_URL = 'https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io';
  const VIDEO_PATH = 'blueprint-to-shipped-fix.webm';

  const pause = (ms) => page.waitForTimeout(ms);
  const chapter = (title, description, duration = 1800) => page.screencast.showChapter(title, { description, duration });
  const overlay = (html, duration = 1800) => page.screencast.showOverlay(html, { duration });
  const note = async (title, body, duration = 2200) => overlay(`
    <div style="position:absolute;top:16px;right:16px;max-width:440px;padding:12px 14px;background:rgba(15,23,42,0.88);color:white;border-radius:12px;font:14px/1.4 sans-serif;box-shadow:0 8px 24px rgba(0,0,0,0.28)">
      <div style="font-weight:700;margin-bottom:6px;">${title}</div>
      <div>${body}</div>
    </div>
  `, duration);
  const prewarmCut = (body) => note('Pre-warm cut', body, 2600);
  const todo = (body) => note('SELECTOR TBD', body, 2600);

  const closeDialogWithEscape = async () => {
    await page.keyboard.press('Escape');
    await pause(500);
  };

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.screencast.start({ path: VIDEO_PATH, size: { width: 1920, height: 1080 } });
  await page.goto(STAGING_URL, { waitUntil: 'domcontentloaded' });
  await note('Auth assumption', 'Run only after a valid human-created staging session exists. This script must not perform OAuth itself.', 2400);
  await pause(1000);

  // Beat 1.1 — Create the project
  await chapter('Create the project', 'Point Agentweaver at an empty GitHub repo and name the project.', 1800);
  await page.getByRole('button', { name: 'Create from GitHub' }).click();
  await pause(500);
  await page.getByRole('textbox', { name: 'Or paste any repository' }).click();
  await page.getByRole('textbox', { name: 'Or paste any repository' }).pressSequentially('https://github.com/sabbour/agentweaver-demo-dryrun', { delay: 45 });
  await pause(400);
  await page.getByRole('button', { name: 'Go →' }).click();
  await pause(500);
  await page.getByRole('textbox', { name: 'Project name' }).click();
  await page.getByRole('textbox', { name: 'Project name' }).pressSequentially('blueprint-demo', { delay: 55 });
  await pause(900);

  // Beat 1.2 — Choose a blueprint
  await chapter('Choose a blueprint', 'Show the Generate option, then cast the Product & Software Delivery team.', 1800);
  await page.getByRole('button', { name: 'Templates', exact: true }).click();
  await pause(600);
  await page.getByRole('button', { name: 'Generate', exact: true }).hover(); // live-verified in the create-project dialog
  await pause(1200);
  await page.getByRole('radio', { name: 'Product & Software Delivery' }).click();
  await pause(900);
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await pause(1200);

  // Beat 1.3 — Inspect the team
  await chapter('Inspect the team', 'Show the agents, marketplace/import paths, then the assignments tab.', 1800);
  await page.getByRole('link', { name: 'Agents', exact: true }).click();
  await pause(700);
  await page.getByRole('link', { name: 'Skills', exact: true }).click();
  await pause(700);
  await page.getByRole('button', { name: 'Browse marketplaces', exact: true }).click(); // live-verified button label
  await pause(1000);
  await closeDialogWithEscape(); // source/test-backed close path; no dedicated close button
  await page.getByRole('button', { name: 'Import skill', exact: true }).click(); // live-verified button label
  await pause(900);
  await closeDialogWithEscape();
  await page.getByRole('tab', { name: 'Assignments', exact: true }).click();
  await pause(1000);

  // Beat 2.1 — Frame the product
  await chapter('Frame the product', 'Pick the product workflow and hand the team a real problem to solve.', 1800);
  await page.getByTestId('start-task-topbar-action').click();
  await pause(500);
  await page.getByLabel('Workflow', { exact: true }).selectOption('software-delivery');
  await pause(500);
  await page.getByRole('textbox', { name: 'Goal' }).click();
  await page.getByRole('textbox', { name: 'Goal' }).fill("Planning a weekend trip with friends turns into a mess of group chats, links, and half-made plans, and everyone ends up on a slightly different page. I want to launch Trailhead, which turns any free weekend into an outdoor trip the whole group actually agrees on. Work out who this is really for, what they need, and how we'd position it — the promise and the value props that make someone want to try it. Then shape the first experience that gets a group from 'we should go somewhere' to a plan they all share. As the simplest thing we can put in front of a real user, stand up a landing page that tells that story with placeholder content: a welcome banner, the three value props as stand-in blurbs, and one primary 'Plan my first trip' button to start.");
  await pause(900);
  await page.getByRole('button', { name: 'Define Outcome', exact: true }).hover();
  await pause(700);
  await page.getByRole('button', { name: 'Define Outcome', exact: true }).click();
  await pause(1000);

  // Beat 2.2 — Review and confirm the plan
  await prewarmCut('Do not record idle polling. Resume this script only when the Outcome plan confirmation panel is already visible.');
  await chapter('Review and confirm the plan', 'Clarify the Outcome plan, allow task promotion, and confirm.', 1800);
  await page.getByRole('button', { name: 'Clarify plan', exact: true }).click();
  await pause(700);
  await page.getByRole('textbox', { name: /^(Feedback|Additional feedback)$/ }).fill("Keep this first slice to the landing page only: the welcome banner, the value props, and the 'Plan my first trip' button. No accounts and no saved trips yet.");
  await pause(700);
  await page.getByRole('button', { name: 'Send', exact: true }).click(); // resolved via source — not live-verified
  await pause(1200);
  await page.getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' }).click();
  await pause(700);
  await page.getByRole('button', { name: 'Confirm plan', exact: true }).hover();
  await pause(700);
  await page.getByRole('button', { name: 'Confirm plan', exact: true }).click();
  await pause(1200);

  // Beat 2.3 — Watch the work plan run
  await prewarmCut('Resume only when the run has reached a reviewable work plan / graph state. Live staging proved decomposition can take ~95-110s after confirm; wait/poll at least 150s before treating it as stuck, and never capture the empty pending gap in real time.');
  await chapter('Watch the work plan run', 'Open the topology graph, step through nodes, then close it.', 1800);
  await page.getByTestId('open-topology-minimap').click();
  await pause(700);
  await page.getByRole('button', { name: /Coordinator/ }).click();
  await pause(800);
  await page.getByRole('button', { name: /Work plan/ }).click();
  await pause(700);
  await page.getByRole('button', { name: /Implement the confirmed outcome/ }).click();
  await pause(700);
  await page.getByRole('button', { name: 'Zoom in' }).click();
  await pause(900);
  await page.getByRole('button', { name: 'Fit to view' }).click();
  await pause(600);
  await page.getByRole('button', { name: 'Close panel' }).click();
  await pause(900);

  // Beat 2.4 — Review the board
  await prewarmCut('Resume only after independent task promotion has surfaced tasks on the board.');
  await chapter('Review the board', 'Show Backlog and Ready, then move the landing-page task into Ready.', 1800);
  await page.getByRole('link', { name: 'Board', exact: true }).click();
  await pause(900);
  await page.getByRole('region', { name: 'Backlog column' }).hover();
  await pause(800);
  await page.getByRole('region', { name: 'Ready column' }).hover();
  await pause(800);
  // SELECTOR TBD: tighten the specific landing-page task card once the live promoted title is known.
  // Source evidence: task cards use runtime data-testid="task-card-<task_id>" and are draggable divs.
  await todo('Live board passes still showed Backlog=0 and Ready=0 with no task-card-* elements, so there is no truthful card to drag yet. Keep this beat blocked until promoted backlog cards actually surface.');
  await pause(1000);

  // Beat 2.5 — Ship it
  await prewarmCut('Resume only when the run has reached human-review notifications / preview-ready state; do not wait in real time.');
  await chapter('Ship it', 'Approve gates as they appear and open the preview when Build & Test is ready.', 1800);
  await page.getByTestId('notification-bell').click();
  await pause(900);
  await page.getByTestId('notification-bell').click();
  await pause(700);
  // SELECTOR TBD: exact intermediate approval gate controls before preview, if any.
  await note('Review gate handling', 'If tool / permission / preview-approval cards are present, approve them before the preview step. Current blocker: the latest execution child proved a healthy forwarded port but the start_preview tool still failed, so no Open preview control surfaced.', 2600);
  await page.getByRole('button', { name: 'Open preview', exact: true }).click(); // resolved via source — not live-verified
  await pause(1400);

  // Beat 2.6 — Approve the merge
  await prewarmCut('Only resume on a dedicated recording environment when the final review gate is ready. Do not use this during a dry-run on shared staging.');
  await chapter('Approve the merge', 'Open the final approval notification, then approve and merge.', 1800);
  await page.getByTestId('notification-bell').click();
  await pause(900);
  await page.getByTestId('notification-bell').click();
  await pause(700);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).hover();
  await pause(700);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).click();
  await pause(1200);

  // Beat 2.7 — Check project health
  await chapter('Check project health', 'See throughput, quality, cost, latency, and traces.', 1800);
  await page.getByRole('link', { name: 'Dashboard', exact: true }).click();
  await pause(900);
  await page.getByRole('heading', { name: 'Operational signals' }).hover();
  await pause(800);
  await page.getByRole('table', { name: 'Agent leaderboard' }).hover();
  await pause(800);
  await page.getByRole('link', { name: 'Observability', exact: true }).click();
  await pause(900);
  await page.getByRole('tab', { name: 'Traces', exact: true }).click();
  await pause(700);
  await page.getByRole('tab', { name: 'Agents', exact: true }).click();
  await pause(1000);

  // Beat 2.8 — Review team memory
  await chapter('Review team memory', 'Show the decisions the run wrote down.', 1800);
  await page.getByRole('link', { name: 'Memories', exact: true }).click();
  await pause(900);
  await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
  await pause(1000);

  // Beat 3.1 — Put it on a schedule
  await chapter('Put it on a schedule', 'Open Workflows and set the workflow to run on a recurring cadence.', 1800);
  await page.getByRole('link', { name: 'Workflows', exact: true }).click(); // resolved via source — not live-verified
  await pause(900);
  await page.getByRole('button', { name: /^(Add|Edit) schedule$/ }).first().click(); // source says scheduling is inline on the row
  await pause(900);
  await page.getByRole('combobox', { name: 'Cadence', exact: true }).click();
  await pause(900);
  await note('Cadence choice', 'Pick the daily / weekly / monthly option that matches the shot plan, then save the schedule.', 2200);

  // Beat 3.2 — Trigger it from GitHub
  await chapter('Trigger it from GitHub', 'Open Settings > Webhooks, generate a secret, and show the payload URL.', 1800);
  await page.getByRole('link', { name: 'Settings', exact: true }).click(); // source nav label; plan text previously said Project Settings
  await pause(900);
  await page.getByRole('button', { name: 'Webhooks', exact: true }).click();
  await pause(900);
  await page.getByRole('button', { name: /^(Generate|Rotate) secret$/ }).click();
  await pause(1200);
  await page.getByRole('textbox', { name: 'Payload URL', exact: true }).click();
  await pause(1000);

  // Beat 4.1 — Pivot to the seeded bug
  await prewarmCut('Keep the GitHub issue as pre-recording setup unless a verified in-app issue surface exists.');
  await chapter('Pivot to the seeded bug', 'Start the repair from the existing GitHub issue.', 1800);
  await todo('Issue-list / linked-issue selector still needs a live validation pass. Keep the GitHub issue setup external for now.');

  // Beat 4.2 — Ask the assistant to triage
  await chapter('Ask the assistant to triage', 'Have the assistant read the issue and start a Bug Fix workflow.', 1800);
  await page.getByRole('button', { name: 'New session', exact: true }).click();
  await pause(700);
  await page.getByRole('textbox', { name: 'Message the assistant...' }).click();
  await page.getByRole('textbox', { name: 'Message the assistant...' }).pressSequentially("Triage https://github.com/sabbour/agentweaver-demo-dryrun/issues/1. Investigate the narrow-tablet welcome-banner overlap, propose a minimal fix and test plan, then use the Bug Fix workflow.", { delay: 35 });
  await pause(900);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await pause(1200);

  // Beat 4.3 — Read and scope the bug
  await prewarmCut('Resume only when the assistant-created bug-fix run has produced its first concrete diagnosis.');
  await chapter('Read and scope the bug', 'Show the diagnosis, expected behavior, and smallest safe fix.', 1800);
  await todo('Bug-output selectors still need a live run to identify the exact surfaces worth framing.');

  // Beat 4.4 — Implement and test the repair
  await prewarmCut('Resume only when code/test artifacts are visible; do not record idle wait.');
  await chapter('Implement and test the repair', 'Show the fix and the tests that prove it.', 1800);
  await todo('Implementation/test evidence selectors for the seeded issue still need a live run.');

  // Beat 4.5 — Preview the repaired behavior
  await prewarmCut('Resume only when the bug-fix preview is already active.');
  await chapter('Preview the repaired behavior', 'Show the narrow-tablet layout working before merge.', 1800);
  await todo('Bug-fix preview surface still needs a live selector mapping pass.');

  // Beat 4.6 — Approve the bug fix
  await prewarmCut('Only resume when the bug-fix review gate is present in the dedicated recording environment.');
  await chapter('Approve the bug fix', 'Make the final merge decision.', 1800);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).hover();
  await pause(700);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).click();
  await pause(1200);

  // Beat 4.7 — Close the loop on the issue
  await chapter('Close the loop on the issue', 'Show the merged PR linked back to the original issue.', 1800);
  await todo('Open the issue-linked PR in a deliberate second github.com tab once that artifact exists.');

  // Beat 5.1 — Drive it from your own tools
  await chapter('Drive it from your own tools', 'Show the MCP clients section and the read-only MCP server URL.', 1800);
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await pause(900);
  await page.getByRole('textbox', { name: 'MCP server URL', exact: true }).hover(); // live-verified
  await pause(1000);
  // SELECTOR TBD: live staging exposes no bearer-token field and no copy control for MCP server URL.
  await todo('The shipped Settings page exposes only the read-only MCP server URL field. There is no bearer-token field and no copy button, so Beat 5.1 must be rewritten before a truthful recording.');

  await page.screencast.stop();
}
