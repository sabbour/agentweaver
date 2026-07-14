export function renderDriverBanner({ contract, p0 }) {
  if (!contract?.ok) return `CONTRACT FAIL: ${contract.results.filter((r) => r.status === 'FAIL').map((r) => `${r.capability}: ${r.message}`).join(' | ')}`;
  if (p0 && !p0.ok) return `DRIVER P0 FAIL: failed turns=${p0.failedTurns.join(',') || 'none'}; pushbacks=${p0.successfulPushbacks}`;
  return 'DRIVE+CAPTURE OK';
}
