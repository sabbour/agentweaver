import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { SourceControlOutputCaptureSummary } from './contracts';
import { RunOutputCapturesPanel } from './RunOutputCaptures';

const mocks = vi.hoisted(() => ({
  listRunOutputCaptures: vi.fn(),
  readRunOutputCaptureDiff: vi.fn(),
  bindings: [] as unknown[],
}));

// One stable apiCall, like the real provider. It records the run binding it was asked for.
const auth = vi.hoisted(() => ({
  apiCall: (operation: (token: string, tenant: string) => unknown, binding: unknown) => {
    mocks.bindings.push(binding);
    return operation('token', 'tenant-1');
  },
}));

vi.mock('./AuthContext', () => ({ useAuth: () => auth }));

vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    listRunOutputCaptures: mocks.listRunOutputCaptures,
    readRunOutputCaptureDiff: mocks.readRunOutputCaptureDiff,
  },
}));

function capture(captureId: string, branchName: string, extra: Partial<SourceControlOutputCaptureSummary> = {}): SourceControlOutputCaptureSummary {
  return {
    captureId, eventId: `e-${captureId}`, eventPosition: 1, capturedAt: '2026-01-02T03:04:05Z', workspaceId: 'w1',
    branchName, baseSha: 'abcdef1234567890', outputTreeSha: 'f'.repeat(40), manifestSha256: 'm', manifestByteLength: 10,
    patchSha256: 'p', patchByteLength: 2048, packageByteLength: 4096, ...extra,
  };
}

function page(captures: SourceControlOutputCaptureSummary[], next: { at: string; id: string } | null = null) {
  return { captures, nextBeforeCapturedAt: next?.at ?? null, nextBeforeCaptureId: next?.id ?? null };
}

function renderPanel() {
  return render(<RunOutputCapturesPanel projectId="p1" runId="r1" sessionId="s1" />);
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.bindings = [];
  mocks.listRunOutputCaptures.mockResolvedValue(page([]));
});

describe('RunOutputCapturesPanel', () => {
  it('lists the saved file sets for the run session', async () => {
    mocks.listRunOutputCaptures.mockResolvedValue(page([
      capture('c1', 'agentweaver/run-1', { patchByteLength: 512 }),
      capture('c2', 'agentweaver/run-1-retry', { patchByteLength: 3 * 1024 * 1024 }),
    ]));
    renderPanel();

    const list = await screen.findByRole('list', { name: 'Saved file sets' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('agentweaver/run-1')).toBeTruthy();
    expect(within(rows[0]).getByText(/base abcdef1 · patch 512 B$/)).toBeTruthy();
    expect(within(rows[1]).getByText(/patch 3\.0 MB$/)).toBeTruthy();
    expect(within(rows[0]).getByRole('button', { name: 'Show changes' })).toBeTruthy();
    expect(mocks.listRunOutputCaptures).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', { limit: 20 }, 'tenant-1');
    // The read is bound to the run, so the service checks the caller owns it.
    expect(mocks.bindings).toEqual([{ projectId: 'p1', runId: 'r1' }]);
  });

  it('loads older file sets with the returned cursor', async () => {
    mocks.listRunOutputCaptures
      .mockResolvedValueOnce(page([capture('c1', 'branch-new')], { at: '2026-01-01T00:00:00Z', id: 'c1' }))
      .mockResolvedValueOnce(page([capture('c0', 'branch-old')]));
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Load older file sets' }));
    expect(await screen.findByText('branch-old')).toBeTruthy();
    expect(screen.getByText('branch-new')).toBeTruthy();
    expect(mocks.listRunOutputCaptures).toHaveBeenLastCalledWith(
      'token', 'p1', 'r1', 's1', { limit: 20, beforeCapturedAt: '2026-01-01T00:00:00Z', beforeCaptureId: 'c1' }, 'tenant-1',
    );
    expect(screen.queryByRole('button', { name: 'Load older file sets' })).toBeNull();
  });

  it('shows the changes of one file set and hides them again', async () => {
    const first = capture('c1', 'agentweaver/run-1');
    mocks.listRunOutputCaptures.mockResolvedValue(page([first]));
    mocks.readRunOutputCaptureDiff.mockResolvedValue({ capture: first, patch: 'diff --git a/README.md b/README.md\n+Hello\n' });
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Show changes' }));
    const patch = await screen.findByLabelText('Changes on agentweaver/run-1');
    expect(patch.textContent).toContain('+Hello');
    expect(mocks.readRunOutputCaptureDiff).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'c1', 'tenant-1');
    expect(mocks.bindings.at(-1)).toEqual({ projectId: 'p1', runId: 'r1' });

    fireEvent.click(screen.getByRole('button', { name: 'Hide changes' }));
    expect(screen.queryByLabelText('Changes on agentweaver/run-1')).toBeNull();
    expect(mocks.readRunOutputCaptureDiff).toHaveBeenCalledTimes(1);
  });

  it('says when a file set has no text changes', async () => {
    const first = capture('c1', 'agentweaver/run-1');
    mocks.listRunOutputCaptures.mockResolvedValue(page([first]));
    mocks.readRunOutputCaptureDiff.mockResolvedValue({ capture: first, patch: '' });
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Show changes' }));
    expect(await screen.findByText('This file set has no text changes.')).toBeTruthy();
  });

  it('shows the empty state', async () => {
    renderPanel();
    expect(await screen.findByText('No files yet')).toBeTruthy();
    expect(screen.getByText('This run has not saved any produced files.')).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Saved file sets' })).toBeNull();
  });

  it('explains a 403 and a 404 in plain copy', async () => {
    mocks.listRunOutputCaptures.mockRejectedValueOnce(new GatewayError(403, {}, 'Forbidden'));
    renderPanel();
    expect(await screen.findByText('You do not have permission to read the files of this run.')).toBeTruthy();

    mocks.listRunOutputCaptures.mockRejectedValueOnce(new GatewayError(404, {}, 'Not found'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText(/^The files of this run are not available\./)).toBeTruthy();
  });

  it('shows other list errors', async () => {
    mocks.listRunOutputCaptures.mockRejectedValue(new GatewayError(500, {}, 'Storage is down'));
    renderPanel();
    expect(await screen.findByText('HTTP 500: Storage is down')).toBeTruthy();
    expect(screen.queryByText('No files yet')).toBeNull();
  });

  it('shows a failed diff read and keeps the list', async () => {
    mocks.listRunOutputCaptures.mockResolvedValue(page([capture('c1', 'agentweaver/run-1')]));
    mocks.readRunOutputCaptureDiff.mockRejectedValue(new GatewayError(500, {}, 'Patch is down'));
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Show changes' }));
    expect(await screen.findByText('HTTP 500: Patch is down')).toBeTruthy();
    expect(screen.getByRole('list', { name: 'Saved file sets' })).toBeTruthy();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Show changes' }).hasAttribute('disabled')).toBe(false));
  });
});
