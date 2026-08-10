import { classifyAppReadiness } from './readiness.mjs';

function readinessFailure(step) {
  const readiness = step.readiness ?? classifyAppReadiness({
    url: step.url,
    domSnapshot: step.domSnapshot,
  });
  if (readiness.state === 'auth-loading') {
    return { kind: 'auth-loading-shell', turn: step.id, evidence: readiness.reason };
  }
  if (readiness.state === 'auth-required') {
    return { kind: 'authentication-required', turn: step.id, evidence: readiness.reason };
  }
  if (['capture', 'goto'].includes(step.action) && readiness.state !== 'ready') {
    return { kind: 'app-not-ready', turn: step.id, evidence: readiness.reason };
  }
  return null;
}

export function computeDriverP0(steps = [], commandFailures = []) {
  const failures = [];
  if (steps.length === 0) failures.push({ kind: 'no-evidence', turn: null, evidence: 'no successful UI evidence steps were recorded' });
  for (const failure of commandFailures) {
    failures.push({
      kind: 'command-failed',
      turn: failure.id ?? null,
      evidence: `${failure.action ?? 'unknown'}: ${failure.message ?? failure.code ?? 'failed'}`,
    });
  }
  for (const step of steps) {
    const terminalFailure = readinessFailure(step);
    if (terminalFailure) failures.push(terminalFailure);
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
  const result = computeDriverP0(transcript.steps, transcript.commandFailures);
  write(result.pass ? 'UI DRIVE+CAPTURE OK\nP1 — UI/UX quality: DEFERRED to LLM judge' : 'UI DRIVER P0 FAIL\nP1 — UI/UX quality: DEFERRED to LLM judge');
  return result;
}
