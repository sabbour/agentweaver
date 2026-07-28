async page => {
  const BASE = 'https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io';
  const PROJECT = 'blueprint-demo-final';
  const GOAL = "Planning a weekend trip with friends turns into a mess of group chats, links, and half-made plans, and everyone ends up on a slightly different page. I want to launch Trailhead, which turns any free weekend into an outdoor trip the whole group actually agrees on. Work out who this is really for, what they need, and how we'd position it — the promise and the value props that make someone want to try it. Then shape the first experience that gets a group from 'we should go somewhere' to a plan they all share. As the simplest thing we can put in front of a real user, stand up a landing page that tells that story with placeholder content: a welcome banner, the three value props as stand-in blurbs, and one primary 'Plan my first trip' button to start.";
  const pause = ms => page.waitForTimeout(ms);
  const chapter = (title, description, duration = 1800) => page.screencast.showChapter(title, { description, duration });

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.screencast.start({ path: '.worktrees/demo-recording-plans/demo-plans/recordings/seg1a-create-define.webm', size: { width: 1920, height: 1080 } });

  await page.goto(`${BASE}/projects`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(12000);

  await chapter('Create the project', 'Create a fresh project from the seeded GitHub repository.', 2000);
  await page.getByRole('button', { name: 'Create from GitHub' }).click();
  await pause(600);
  await page.getByRole('textbox', { name: 'Or paste any repository' }).pressSequentially('https://github.com/sabbour/agentweaver-demo-dryrun', { delay: 40 });
  await pause(400);
  await page.getByRole('button', { name: 'Go →' }).click();
  await pause(700);
  await page.getByRole('textbox', { name: 'Project name' }).fill(PROJECT);
  await pause(600);

  await chapter('Choose a blueprint', 'Cast the Product & Software Delivery team.', 1800);
  await page.getByRole('button', { name: 'Templates', exact: true }).click();
  await pause(500);
  await page.getByRole('button', { name: 'Generate', exact: true }).hover();
  await pause(1000);
  await page.getByRole('radio', { name: 'Product & Software Delivery' }).click();
  await pause(700);
  await page.getByRole('button', { name: 'Create', exact: true }).click();

  const projectCard = page.getByRole('listitem').filter({ has: page.getByText(PROJECT, { exact: true }) }).first();
  await projectCard.getByRole('button', { name: /^Open$/ }).click();
  await page.waitForURL(/\/projects\/[^/]+$/, { timeout: 120000 });
  await pause(1500);

  await chapter('Frame the product', 'Start the workflow and define the desired outcome.', 2000);
  await page.getByTestId('start-task-topbar-action').click();
  await pause(500);
  await page.getByLabel('Workflow', { exact: true }).selectOption('software-delivery');
  await pause(500);
  await page.getByRole('textbox', { name: 'Goal' }).fill(GOAL);
  await pause(800);
  await page.getByRole('button', { name: 'Define Outcome', exact: true }).click();

  await page.waitForURL(/\/projects\/[^/]+\/orchestrations\/[^/]+$/, { timeout: 120000 });
  const result = { url: page.url(), projectName: PROJECT };
  await pause(1200);
  await page.screencast.stop();
  return result;
}
