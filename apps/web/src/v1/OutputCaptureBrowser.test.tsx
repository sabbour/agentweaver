import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { gatewayClient, GatewayError } from './api';
import { captureDetail, capturedPath, capturedText } from './__fixtures__/outputCapture';
import { OutputCaptureBrowser } from './OutputCaptureBrowser';
import type { SessionTreeSnapshot } from './contracts';

const auth = vi.hoisted(() => ({
  session: { accessToken: 'current-token' },
  authorizationContext: { tenantId: 'tenant-1', membershipRevision: 1 },
  ensureSession: vi.fn().mockResolvedValue(true),
  authorize: vi.fn().mockResolvedValue(undefined),
  apiCall: vi.fn((operation: (token: string, tenant: string) => Promise<unknown>, binding?: unknown) => {
    if (!binding) throw new Error('Capture reads require a run binding.');
    return operation('bound-token', 'tenant-1');
  }),
}));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));

const detail = captureDetail();
const tree: SessionTreeSnapshot = {
  rootSessionId: 'root',
  nodes: ['root', 'child'].map((sessionId) => ({
    identity: { projectId: 'p1', runId: 'r-1', sessionId }, rootSessionId: 'root',
    kind: 'coordinator', detached: false, lifecycle: 'active', executionFence: 1,
    logicalTurnOrdinal: 1, stateVersion: 1, createdAt: '2026-10-20T12:00:00Z',
  })),
};
beforeEach(() => {
  vi.restoreAllMocks();
  auth.apiCall.mockClear();
  auth.session.accessToken = 'current-token';
  auth.authorizationContext.membershipRevision = 1;
  vi.spyOn(gatewayClient, 'getRunStatus').mockResolvedValue({
    projectId: 'p1', runId: 'r-1', rootSessionId: 'root', executionFence: 1,
    logicalTurnOrdinal: 1, executionState: 'active', stateVersion: 1,
  });
  vi.spyOn(gatewayClient, 'getSessionTree').mockResolvedValue(tree);
  vi.spyOn(gatewayClient, 'listSourceControlOutputCaptures').mockResolvedValue({ captures: [detail.capture] });
  vi.spyOn(gatewayClient, 'readSourceControlOutputCapture').mockResolvedValue(detail);
  vi.spyOn(gatewayClient, 'readSourceControlOutputCaptureFile').mockResolvedValue(new TextEncoder().encode(capturedText).buffer);
});

async function selectCapture() {
  const picker = await screen.findByLabelText('Saved capture');
  fireEvent.change(picker, { target: { value: 'capture-1' } });
  await screen.findByRole('region', { name: 'Capture details' });
}
async function openFile() {
  fireEvent.click(await screen.findByText('release notes.md'));
}

describe('OutputCaptureBrowser', () => {
  it('reads authorized sessions, manifests and verified files through the retained 0.x viewer, then links the capture', async () => {
    const select = vi.fn();
    render(<OutputCaptureBrowser projectId="p1" runId="r-1" onSelect={select} />);
    await selectCapture();
    await openFile();
    await screen.findByRole('heading', { name: 'Release notes' });
    fireEvent.click(screen.getByRole('tab', { name: 'Source' }));
    expect(screen.getByRole('tab', { name: 'Source' }).getAttribute('aria-selected')).toBe('true');
    expect(gatewayClient.readSourceControlOutputCaptureFile).toHaveBeenCalledWith(
      'bound-token', 'p1', 'r-1', 'root', 'capture-1', capturedPath, 'tenant-1');
    fireEvent.click(screen.getByRole('button', { name: 'Link capture' }));
    expect(select).toHaveBeenCalledExactlyOnceWith('capture-1');
    expect(auth.apiCall.mock.calls.every((call) => JSON.stringify(call[1]) === JSON.stringify({ projectId: 'p1', runId: 'r-1' }))).toBe(true);
  });

  it('loads the next capture page with both cursor fields and preserves previous choices', async () => {
    vi.mocked(gatewayClient.listSourceControlOutputCaptures)
      .mockResolvedValueOnce({ captures: [detail.capture], nextBeforeCapturedAt: detail.capture.capturedAt, nextBeforeCaptureId: 'capture-1' })
      .mockResolvedValueOnce({ captures: [captureDetail('r-1', 'capture-2').capture] });
    render(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Load more captures' }));
    await waitFor(() => expect(screen.getAllByRole('option').some((option) => option.getAttribute('value') === 'capture-2')).toBe(true));
    expect(gatewayClient.listSourceControlOutputCaptures).toHaveBeenLastCalledWith(
      'bound-token', 'p1', 'r-1', 'root', { capturedAt: detail.capture.capturedAt, captureId: 'capture-1' }, 'tenant-1');
    expect(screen.getAllByRole('option').some((option) => option.getAttribute('value') === 'capture-1')).toBe(true);
  });

  it('shows empty and failed lists explicitly and retries a denied list without stale choices', async () => {
    vi.mocked(gatewayClient.listSourceControlOutputCaptures)
      .mockRejectedValueOnce(new GatewayError(403, { error: 'project_read_permission_denied' }, 'You can no longer read this project.'))
      .mockResolvedValueOnce({ captures: [] });
    render(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    await screen.findByRole('alert');
    expect(screen.queryByLabelText('Saved capture')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByText('No saved captures');
  });

  it('does not display bytes whose hash or length differs from the manifest', async () => {
    vi.mocked(gatewayClient.readSourceControlOutputCaptureFile).mockResolvedValue(new TextEncoder().encode('tampered bytes').buffer);
    render(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    await selectCapture();
    await openFile();
    await screen.findAllByText('The saved file could not be verified. Choose the capture again.');
    expect(screen.queryByRole('heading', { name: 'Release notes' })).toBeNull();
  });

  it('rejects a manifest outside the selected run', async () => {
    vi.mocked(gatewayClient.readSourceControlOutputCapture).mockResolvedValue(captureDetail('another-run'));
    render(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    fireEvent.change(await screen.findByLabelText('Saved capture'), { target: { value: 'capture-1' } });
    await screen.findByText('The capture does not match this run. Choose it again.');
    expect(screen.queryByRole('region', { name: 'Captured files' })).toBeNull();
  });

  it('discards a late detail when switching sessions and removes content on authority change', async () => {
    let finish!: (value: typeof detail) => void;
    vi.mocked(gatewayClient.readSourceControlOutputCapture).mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    fireEvent.change(await screen.findByLabelText('Saved capture'), { target: { value: 'capture-1' } });
    await waitFor(() => expect(finish).toBeDefined());
    fireEvent.change(screen.getByLabelText('Session'), { target: { value: 'child' } });
    await act(async () => finish(detail));
    expect(screen.queryByRole('region', { name: 'Captured files' })).toBeNull();
    await selectCapture();
    await openFile();
    await screen.findByRole('heading', { name: 'Release notes' });
    vi.mocked(gatewayClient.getRunStatus).mockRejectedValue(new GatewayError(403, {}, 'Read access was removed.'));
    auth.authorizationContext.membershipRevision++;
    view.rerender(<OutputCaptureBrowser projectId="p1" runId="r-1" />);
    expect(screen.queryByRole('heading', { name: 'Release notes' })).toBeNull();
    await screen.findByText(/Read access was removed\./);
  });
});
