/**
 * Classify the result of a poll-run call to determine the next action in the
 * smoke-test state machine.
 *
 * Returns one of:
 *   'break'   — run reached a terminal status; exit the poll loop.
 *   'confirm' — coordinator is at the awaiting_confirmation gate and has not yet
 *               been confirmed; caller should call coordinator_outcome_spec_confirm
 *               then continue polling.
 *   'continue' — non-terminal, no gate; keep polling.
 *
 * @param {object|null} content - structuredContent from the poll-run response.
 * @param {{ terminal: Set<string>, alreadyConfirmed?: boolean }} opts
 * @returns {'break' | 'confirm' | 'continue'}
 */
export function classifySmokeStatus(content, { terminal, alreadyConfirmed = false }) {
  const status = String(content?.status ?? '').toLowerCase();
  const coordinatorStatus = String(content?.coordinator_status ?? '').toLowerCase();
  const terminalCoordinator = new Set(['assembly_blocked', 'assembly_failed', 'assembly_declined']);
  if (terminal.has(status) || terminalCoordinator.has(coordinatorStatus)) return 'break';
  if (coordinatorStatus === 'awaiting_confirmation' && !alreadyConfirmed) return 'confirm';
  return 'continue';
}
