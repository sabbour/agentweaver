async page => {
  const pause = ms => page.waitForTimeout(ms);
  const chapter = (title, description, duration = 1800) => page.screencast.showChapter(title, { description, duration });

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.screencast.start({ path: '.worktrees/demo-recording-plans/demo-plans/recordings/seg2-board-review.webm', size: { width: 1920, height: 1080 } });

  await chapter('Review the board', 'Open the board and show the flow columns before returning to the orchestration.', 1800);
  await page.getByRole('link', { name: 'Board', exact: true }).click();
  await pause(1200);
  await page.getByRole('region', { name: 'Backlog column' }).hover();
  await pause(800);
  await page.getByRole('region', { name: 'Ready column' }).hover();
  await pause(800);
  await page.getByRole('region', { name: 'Active column' }).hover();
  await pause(900);

  await chapter('Review changes', 'Return to the orchestration and show the live review gate.', 1800);
  await page.getByRole('link', { name: 'Orchestrations', exact: true }).click();
  await pause(1200);
  await page.getByRole('link', { name: /Planning a weekend trip with friends/i }).click();
  await pause(2000);
  await page.getByRole('button', { name: 'Review changes' }).click();
  await pause(1200);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).hover();
  await pause(1200);

  const result = { url: page.url() };
  await page.screencast.stop();
  return result;
}
