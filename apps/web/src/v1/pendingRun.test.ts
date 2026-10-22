import { afterEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import {
  clearPendingRun,
  createRunError,
  cancelRunError,
  needsNewCreateKey,
  ORCHESTRATOR_ROLE_REQUIRED,
  PENDING_RUN_MAX_AGE_MS,
  PENDING_RUN_STORAGE_KEY,
  readPendingRun,
  resumePendingRun,
  runChatPath,
  savePendingRun,
  START_ATTEMPTS,
  STOP_ROLE_REQUIRED,
} from './pendingRun';

const run = { projectId: 'p 1', runId: 'r1', createdAt: '2026-01-01T00:00:00.000Z' };
const created = Date.parse(run.createdAt);

function gatewayError(status: number, code?: string) {
  return new GatewayError(status, code ? { code } : {}, `failed ${status}`);
}

describe('pending run storage', () => {
  afterEach(() => sessionStorage.clear());

  it('saves, reads, and clears one run', () => {
    savePendingRun(run);
    expect(readPendingRun(created + 1000)).toEqual(run);
    clearPendingRun();
    expect(readPendingRun(created)).toBeNull();
  });

  it('drops malformed and expired entries', () => {
    sessionStorage.setItem(PENDING_RUN_STORAGE_KEY, '{bad json');
    expect(readPendingRun(created)).toBeNull();
    expect(sessionStorage.getItem(PENDING_RUN_STORAGE_KEY)).toBeNull();
    sessionStorage.setItem(PENDING_RUN_STORAGE_KEY, JSON.stringify({ projectId: 'p', runId: '' , createdAt: run.createdAt }));
    expect(readPendingRun(created)).toBeNull();
    savePendingRun(run);
    expect(readPendingRun(created + PENDING_RUN_MAX_AGE_MS + 1)).toBeNull();
    expect(sessionStorage.getItem(PENDING_RUN_STORAGE_KEY)).toBeNull();
  });

  it('builds the encoded Chat tab path', () => {
    expect(runChatPath('p 1', 'r/1')).toBe('/projects/p%201/runs/r%2F1?view=chat');
  });
});

describe('error copy', () => {
  it('maps create and cancel denials to plain copy', () => {
    expect((createRunError(gatewayError(403)) as Error).message).toBe(ORCHESTRATOR_ROLE_REQUIRED);
    expect((createRunError(gatewayError(409, 'run_selection_conflict')) as Error).message).toMatch(/settings changed/);
    expect((cancelRunError(gatewayError(403)) as Error).message).toBe(STOP_ROLE_REQUIRED);
    const other = gatewayError(500);
    expect(createRunError(other)).toBe(other);
    expect(cancelRunError(other)).toBe(other);
  });

  it('asks for a new create key only after a conflict', () => {
    expect(needsNewCreateKey(gatewayError(409, 'idempotency_conflict'))).toBe(true);
    expect(needsNewCreateKey(gatewayError(504))).toBe(false);
    expect(needsNewCreateKey(new Error('x'))).toBe(false);
  });
});

describe('resumePendingRun', () => {
  afterEach(() => sessionStorage.clear());

  it('stops before start when a sign-in is needed and keeps the record', async () => {
    savePendingRun(run);
    const start = vi.fn();
    await expect(resumePendingRun({ ensureRunSession: async () => false, start })).resolves.toBe('needs-sign-in');
    expect(start).not.toHaveBeenCalled();
    expect(readPendingRun(created)).toEqual(run);
  });

  it('starts and clears the record', async () => {
    savePendingRun(run);
    const start = vi.fn().mockResolvedValue({ state: 'started' });
    await expect(resumePendingRun({ ensureRunSession: async () => true, start })).resolves.toBe('started');
    expect(start).toHaveBeenCalledTimes(1);
    expect(readPendingRun(created)).toBeNull();
  });

  it('retries the same run after a start timeout', async () => {
    savePendingRun(run);
    const sleep = vi.fn().mockResolvedValue(undefined);
    const start = vi.fn()
      .mockRejectedValueOnce(gatewayError(504, 'run_start_timeout'))
      .mockResolvedValueOnce({ state: 'started' });
    await expect(resumePendingRun({ ensureRunSession: async () => true, start, sleep })).resolves.toBe('started');
    expect(start).toHaveBeenCalledTimes(2);
    expect(sleep).toHaveBeenCalledWith(1000);
  });

  it('gives up after the bounded attempts and keeps the record', async () => {
    savePendingRun(run);
    const start = vi.fn().mockRejectedValue(gatewayError(504, 'run_start_timeout'));
    await expect(resumePendingRun({ ensureRunSession: async () => true, start, sleep: async () => undefined }))
      .rejects.toThrow('did not start in time');
    expect(start).toHaveBeenCalledTimes(START_ATTEMPTS);
    expect(readPendingRun(created)).toEqual(run);
  });

  it('treats an ended run as done and clears the record', async () => {
    savePendingRun(run);
    const start = vi.fn().mockRejectedValue(gatewayError(409, 'run_terminal'));
    await expect(resumePendingRun({ ensureRunSession: async () => true, start })).resolves.toBe('ended');
    expect(readPendingRun(created)).toBeNull();
  });

  it('clears a missing run and keeps other failures', async () => {
    savePendingRun(run);
    await expect(resumePendingRun({ ensureRunSession: async () => true, start: () => Promise.reject(gatewayError(404)) }))
      .rejects.toThrow('could not be found');
    expect(readPendingRun(created)).toBeNull();
    savePendingRun(run);
    const failure = gatewayError(503);
    await expect(resumePendingRun({ ensureRunSession: async () => true, start: () => Promise.reject(failure) })).rejects.toBe(failure);
    expect(readPendingRun(created)).toEqual(run);
  });
});
