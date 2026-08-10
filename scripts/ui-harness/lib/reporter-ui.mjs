export function computeDriverP0(steps = []) {
  const failures = [];
  for (const step of steps) {
    if (step.outcome === 'failed') failures.push({ kind: 'action-failed', turn: step.id, evidence: step.error?.message ?? step.action });
    for (const assertion of step.assertions ?? []) {
      if (assertion.required === true && assertion.observed !== true) failures.push({ kind: 'required-element-missing', turn: step.id, evidence: assertion.target });
    }
    for (const entry of step.console ?? []) if (entry.type === 'error') failures.push({ kind: 'console-error', turn: step.id, evidence: entry.text });
    for (const request of step.network ?? []) {
      if (request.userFacing && Number(request.status) >= 400) failures.push({ kind: 'user-facing-network-error', turn: step.id, evidence: `${request.method} ${request.url} -> ${request.status}` });
    }
  }
  return { pass: failures.length === 0, failures };
}

export function reportDriverP0(transcript, write = console.log) {
  const result = computeDriverP0(transcript.steps);
  write(result.pass ? 'UI DRIVE+CAPTURE OK\nP1 — UI/UX quality: DEFERRED to LLM judge' : 'UI DRIVER P0 FAIL\nP1 — UI/UX quality: DEFERRED to LLM judge');
  return result;
}
