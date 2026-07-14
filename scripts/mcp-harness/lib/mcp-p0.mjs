export function computeMcpP0(transcript, { requiredPushbacks = 2 } = {}) {
  const turns = transcript?.turns ?? [];
  const failures = turns.filter((turn) => turn?.mcp?.isError || turn?.mcp?.protocolErrorCode != null);
  const pushbacks = turns.filter((turn) => /pushback|revise|steer/i.test(`${turn?.thought ?? ''} ${turn?.note ?? ''}`) && turn?.outcome?.ok);
  return { allCallsSucceeded: failures.length === 0, successfulPushbacks: pushbacks.length, pushbackRequirementMet: pushbacks.length >= requiredPushbacks, failedTurns: failures.map((turn) => turn.n), ok: failures.length === 0 && pushbacks.length >= requiredPushbacks };
}
