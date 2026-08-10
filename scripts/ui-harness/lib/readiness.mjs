import { isAuthExpired } from './auth.mjs';
import { structuredDomSnapshot } from './evidence.mjs';

export const DEFAULT_READINESS_TIMEOUT_MS = 30_000;
const DEFAULT_POLL_INTERVAL_MS = 200;
const APP_SHELL_TARGET = Object.freeze({ role: 'main', name: 'Main content' });
const AUTH_LOADING_NAME = /\b(?:loading|checking|verifying|restoring)\b.*\b(?:sign[- ]?in|auth(?:entication)?|session|account)\b|\b(?:sign[- ]?in|auth(?:entication)?|session|account)\b.*\b(?:loading|checking|verifying|restoring)\b/i;
const AUTH_PROMPT_NAME = /\b(?:sign in|log in|authentication required|session expired)\b/i;

function visibleElements(domSnapshot = []) {
  return domSnapshot.filter((element) => element?.visible === true);
}

function matchesTarget(element, target) {
  if (!target) return false;
  if (target.testId) return element.testId === target.testId;
  return element.role === target.role && element.name === target.name;
}

export function classifyAppReadiness({ url = '', domSnapshot = [], target = null } = {}) {
  if (isAuthExpired({ url })) {
    return { state: 'auth-required', reason: 'browser navigated to an authentication route' };
  }

  const visible = visibleElements(domSnapshot);
  if (visible.some((element) => matchesTarget(element, APP_SHELL_TARGET))) {
    return { state: 'ready', target: APP_SHELL_TARGET };
  }

  const authLoading = visible.find((element) =>
    ['progressbar', 'status'].includes(element.role) && AUTH_LOADING_NAME.test(String(element.name ?? '')));
  if (authLoading) {
    return { state: 'auth-loading', reason: authLoading.name };
  }

  const authPrompt = visible.find((element) => AUTH_PROMPT_NAME.test(String(element.name ?? '')));
  if (authPrompt) {
    return { state: 'auth-required', reason: authPrompt.name };
  }

  if (target && visible.some((element) => matchesTarget(element, target))) {
    return { state: 'ready', target };
  }

  const loading = visible.find((element) =>
    ['progressbar', 'status'].includes(element.role) || /^\s*loading\b/i.test(String(element.name ?? '')));
  if (loading) {
    return { state: 'loading', reason: loading.name ?? loading.role };
  }

  return { state: 'not-ready', reason: 'authenticated app shell or declared readiness target is not visible' };
}

function readinessError(result, timeout) {
  const authFailure = result.state === 'auth-loading' || result.state === 'auth-required';
  const prefix = authFailure ? 'AUTH_EXPIRED' : 'APP_NOT_READY';
  const error = new Error(`${prefix}: ${result.reason}; readiness timed out after ${timeout}ms`);
  error.code = authFailure ? 'AUTH_EXPIRED' : 'APP_NOT_READY';
  error.readiness = result;
  return error;
}

export async function waitForAppReadiness(page, {
  timeout = DEFAULT_READINESS_TIMEOUT_MS,
  pollInterval = DEFAULT_POLL_INTERVAL_MS,
  target = null,
  snapshotPage = structuredDomSnapshot,
} = {}) {
  const timeoutMs = Math.max(0, Number(timeout));
  const deadline = Date.now() + timeoutMs;
  let result;

  do {
    result = classifyAppReadiness({
      url: page.url(),
      domSnapshot: await snapshotPage(page),
      target,
    });
    if (result.state === 'ready') return result;
    if (result.state === 'auth-required') throw readinessError(result, timeoutMs);
    if (Date.now() >= deadline) break;
    await page.waitForTimeout(Math.min(pollInterval, Math.max(1, deadline - Date.now())));
  } while (true);

  throw readinessError(result, timeoutMs);
}
