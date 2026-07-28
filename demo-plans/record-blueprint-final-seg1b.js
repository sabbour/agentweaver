async page => {
  const FEEDBACK = "Keep this first slice to the landing page only: the welcome banner, the value props, and the 'Plan my first trip' button. No accounts and no saved trips yet.";
  const pause = ms => page.waitForTimeout(ms);
  const chapter = (title, description, duration = 1800) => page.screencast.showChapter(title, { description, duration });

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.screencast.start({ path: '.worktrees/demo-recording-plans/demo-plans/recordings/seg1b-confirm-plan.webm', size: { width: 1920, height: 1080 } });

  await chapter('Review and confirm the plan', 'Clarify the OutcomeSpec, allow task promotion, and confirm.', 2000);
  await page.getByRole('button', { name: 'Clarify plan', exact: true }).click();
  await pause(700);
  const inline = page.getByRole('textbox', { name: 'Message coordinator...' });
  const additional = page.getByRole('textbox', { name: 'Additional feedback' });
  if (await inline.count()) await inline.fill(`Clarify the outcome plan: ${FEEDBACK}`);
  else if (await additional.count()) await additional.fill(FEEDBACK);
  else await page.getByRole('textbox', { name: 'Feedback' }).fill(FEEDBACK);
  await pause(700);
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await pause(1200);
  await page.getByRole('checkbox', { name: 'Independent task promotion Allow standalone backlog tasks for independent deliverables' }).click();
  await pause(700);
  await page.getByRole('button', { name: 'Confirm plan', exact: true }).hover();
  await pause(800);
  await page.getByRole('button', { name: 'Confirm plan', exact: true }).click();
  await pause(1000);
  const result = { url: page.url() };
  await page.screencast.stop();
  return result;
}
