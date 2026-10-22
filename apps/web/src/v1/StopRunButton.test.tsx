import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { StopRunButton } from './StopRunButton';

const mocks = vi.hoisted(() => ({
  cancelProjectRun: vi.fn(),
  ensureSession: vi.fn(),
}));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return { ...actual, gatewayClient: { cancelProjectRun: mocks.cancelProjectRun } };
});

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    ensureSession: mocks.ensureSession,
    // Pass the unbound token marker and a tenant so the call shape is checked.
    apiCall: (operation: (token: string, tenant: string | null) => Promise<unknown>, binding: unknown) =>
      binding === null ? operation('unbound-token', 'tenant-1') : Promise.reject(new Error('wrong session')),
  }),
}));

async function confirmStop() {
  fireEvent.click(screen.getByRole('button', { name: 'Stop run' }));
  const dialog = await screen.findByRole('dialog');
  expect(dialog.textContent).toContain('Stop this run?');
  expect(dialog.textContent).toContain('Agents stop working and the run ends.');
  await act(async () => {
    fireEvent.click(screen.getAllByRole('button', { name: 'Stop run' }).at(-1)!);
  });
}

describe('StopRunButton', () => {
  beforeEach(() => {
    mocks.cancelProjectRun.mockReset();
    mocks.ensureSession.mockReset().mockResolvedValue(true);
  });

  it.each(['completed', 'failed', 'indeterminate', null])('hides for state %s', (state) => {
    render(<StopRunButton projectId="p1" runId="r1" state={state} />);
    expect(screen.queryByRole('button', { name: 'Stop run' })).toBeNull();
  });

  it('stops an open run with the unbound session after confirmation', async () => {
    const result = { runId: 'r1', state: 'cancelled', cancelledAt: '2026-01-01T00:00:00Z', alreadyTerminal: false };
    mocks.cancelProjectRun.mockResolvedValue(result);
    const onStopped = vi.fn();
    render(<StopRunButton projectId="p1" runId="r1" state="active" onStopped={onStopped} />);
    await confirmStop();
    expect(mocks.ensureSession).toHaveBeenCalledWith(null, false);
    expect(mocks.cancelProjectRun).toHaveBeenCalledWith('unbound-token', 'p1', 'r1', expect.any(String), undefined, 'tenant-1');
    expect(onStopped).toHaveBeenCalledWith(result);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('shows plain copy for a 403 and reuses the key on retry', async () => {
    mocks.cancelProjectRun
      .mockRejectedValueOnce(new GatewayError(403, { code: 'run_cancel_permission_denied' }, 'denied'))
      .mockResolvedValueOnce({ runId: 'r1', state: 'completed', cancelledAt: null, alreadyTerminal: true });
    const onStopped = vi.fn();
    render(<StopRunButton projectId="p1" runId="r1" state="blocked" onStopped={onStopped} />);
    await confirmStop();
    expect(await screen.findByText('You need the Contributor or Owner role to stop runs.')).toBeTruthy();
    await act(async () => {
      fireEvent.click(screen.getAllByRole('button', { name: 'Stop run' }).at(-1)!);
    });
    expect(mocks.cancelProjectRun).toHaveBeenCalledTimes(2);
    expect(mocks.cancelProjectRun.mock.calls[1][3]).toBe(mocks.cancelProjectRun.mock.calls[0][3]);
    expect(onStopped).toHaveBeenCalledWith(expect.objectContaining({ alreadyTerminal: true }));
  });

  it('does not call stop when the project session cannot be restored', async () => {
    mocks.ensureSession.mockResolvedValue(false);
    render(<StopRunButton projectId="p1" runId="r1" state="idle" />);
    await confirmStop();
    expect(mocks.cancelProjectRun).not.toHaveBeenCalled();
    expect(await screen.findByText(/Sign in again, then stop the run/)).toBeTruthy();
  });
});
