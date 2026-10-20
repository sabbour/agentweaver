import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SourceControlOutputCaptureSummary } from './contracts';

const gateway = vi.hoisted(() => ({
  listSourceControlOutputCaptures: vi.fn(),
  readSourceControlOutputCapture: vi.fn(),
  readSourceControlOutputCaptureDiff: vi.fn(),
  readSourceControlOutputCaptureFile: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    apiCall: <T,>(operation: (token: string, tenantSelector: string | null) => Promise<T>) => operation('token', null),
  }),
}));

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, gatewayClient: gateway };
});

const { ProducedFilesPanel } = await import('./ProducedFilesPanel');
const { decodeCapturedFile } = await import('./producedFiles');
const { GatewayError } = await import('./api');

const capture: SourceControlOutputCaptureSummary = {
  captureId: 'capture-1',
  eventId: 'event-1',
  eventPosition: 4,
  capturedAt: '2026-01-02T03:04:05Z',
  workspaceId: 'workspace-1',
  branchName: 'agentweaver/run-1',
  baseSha: 'a'.repeat(40),
  outputTreeSha: 'b'.repeat(40),
  manifestSha256: 'c'.repeat(64),
  manifestByteLength: 10,
  patchSha256: 'd'.repeat(64),
  patchByteLength: 10,
  packageByteLength: 20,
};

describe('ProducedFilesPanel', () => {
  beforeEach(() => {
    Object.values(gateway).forEach((mock) => mock.mockReset());
  });

  it('shows an empty state when the run has no captures', async () => {
    gateway.listSourceControlOutputCaptures.mockResolvedValue({ captures: [] });
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" />);
    expect(await screen.findByText('No produced files yet')).toBeTruthy();
  });

  it('explains that captures are withheld when the owner returns not found', async () => {
    gateway.listSourceControlOutputCaptures.mockRejectedValue(new GatewayError(404, { code: 'not_found' }, 'Not found'));
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" />);
    expect(await screen.findByText('Produced files are not available')).toBeTruthy();
  });

  it('lists the newest capture files, shows file content, and shows the diff', async () => {
    gateway.listSourceControlOutputCaptures.mockResolvedValue({ captures: [capture] });
    gateway.readSourceControlOutputCapture.mockResolvedValue({
      capture,
      manifest: {
        contractVersion: 1, workspaceId: 'workspace-1', runId: 'r1', repositoryId: 'repo-1',
        resourceGeneration: 1, workspaceIncarnationId: 'inc-1', branchName: capture.branchName,
        baseSha: capture.baseSha, outputTreeSha: capture.outputTreeSha,
        files: [{ path: 'README.txt', mode: '100644', sha256: 'e'.repeat(64), byteLength: 5 }],
      },
    });
    gateway.readSourceControlOutputCaptureFile.mockResolvedValue({ bytes: new TextEncoder().encode('hello'), sha256: 'e'.repeat(64) });
    gateway.readSourceControlOutputCaptureDiff.mockResolvedValue({ capture, patch: 'diff --git a/README.txt b/README.txt\n+hello\n' });

    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" />);

    expect(await screen.findByRole('button', { name: 'README.txt' })).toBeTruthy();
    await waitFor(() => expect(gateway.readSourceControlOutputCaptureFile)
      .toHaveBeenCalledWith('token', 'p1', 'r1', 's1', null, 'capture-1', 'README.txt'));
    expect(screen.queryByText('capture-1')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Changes' }));
    await waitFor(() => expect(gateway.readSourceControlOutputCaptureDiff).toHaveBeenCalledTimes(1));
  });
});

describe('decodeCapturedFile', () => {
  it('decodes UTF-8 text and flags binary bytes', () => {
    expect(decodeCapturedFile('a.txt', new TextEncoder().encode('hi'))).toMatchObject({ content: 'hi', is_binary: false });
    expect(decodeCapturedFile('a.bin', new Uint8Array([0, 1]))).toMatchObject({ content: null, is_binary: true });
    expect(decodeCapturedFile('b.bin', new Uint8Array([0xff, 0xfe]))).toMatchObject({ content: null, is_binary: true });
  });
});
