async page => {
  const pause = ms => page.waitForTimeout(ms);
  const chapter = (title, description, duration = 1800) => page.screencast.showChapter(title, { description, duration });

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.screencast.start({ path: '.worktrees/demo-recording-plans/demo-plans/recordings/seg3-approve-merge.webm', size: { width: 1920, height: 1080 } });

  await chapter('Approve and merge', 'Approve the assembled result and let the orchestration finish.', 1800);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).hover();
  await pause(900);
  await page.getByRole('button', { name: 'Approve & merge', exact: true }).click();
  await pause(2000);
  await chapter('Finishing the run', 'Wait for merge and scribe to complete, then show the terminal state.', 1800);
  await pause(3000);

  const result = { url: page.url() };
  await page.screencast.stop();
  return result;
}
