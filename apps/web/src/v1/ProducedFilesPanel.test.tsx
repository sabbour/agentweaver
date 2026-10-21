import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const gateway = vi.hoisted(() => ({
  readSourceControlOutputCapture: vi.fn(),
  readSourceControlOutputCaptureFile: vi.fn(),
}));

const apiCall = <T,>(operation: (token: string, tenantSelector: string | null) => Promise<T>) => operation('token', null);
vi.mock('./AuthContext', () => ({ useAuth: () => ({ apiCall }) }));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: gateway,
}));

const { ProducedFilesPanel } = await import('./ProducedFilesPanel');
const { decodeCapturedFile } = await import('./producedFiles');
const { splitCapturePatch } = await import('./producedFiles');

function manifest(paths: string[]) {
  return { capture: { captureId: 'capture-1' }, manifest: { files: paths.map((path) => ({ path })) } };
}

describe('ProducedFilesPanel', () => {
  beforeEach(() => {
    Object.values(gateway).forEach((mock) => mock.mockReset());
    gateway.readSourceControlOutputCaptureFile.mockResolvedValue({ bytes: new TextEncoder().encode('hello'), sha256: null });
  });

  it('shows an empty state for a manifest with no files', async () => {
    gateway.readSourceControlOutputCapture.mockResolvedValue(manifest([]));
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="capture-1" />);
    expect(await screen.findByText('This save has no files.')).toBeTruthy();
  });

  it('surfaces a failed manifest request', async () => {
    gateway.readSourceControlOutputCapture.mockRejectedValue(new Error('File set unavailable'));
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="capture-1" />);
    expect(await screen.findByText('File set unavailable')).toBeTruthy();
  });

  it('reads manifest paths in the chosen capture using the 0.x file viewer', async () => {
    gateway.readSourceControlOutputCapture.mockResolvedValue(manifest(['README.txt', 'src/index.ts']));
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="capture-1" />);
    expect(await screen.findByRole('button', { name: 'README.txt' })).toBeTruthy();
    await waitFor(() => expect(gateway.readSourceControlOutputCaptureFile)
      .toHaveBeenCalledWith('token', 'p1', 'r1', 's1', null, 'capture-1', 'README.txt'));
    fireEvent.click(screen.getByRole('button', { name: 'index.ts' }));
    await waitFor(() => expect(gateway.readSourceControlOutputCaptureFile)
      .toHaveBeenLastCalledWith('token', 'p1', 'r1', 's1', null, 'capture-1', 'src/index.ts'));
    expect(screen.queryByText('capture-1')).toBeNull();
  });

  it('ignores an old capture response after switching captures', async () => {
    let resolveOld!: (value: ReturnType<typeof manifest>) => void;
    gateway.readSourceControlOutputCapture
      .mockReturnValueOnce(new Promise((resolve) => { resolveOld = resolve; }))
      .mockResolvedValueOnce(manifest(['new.txt']));
    const { rerender } = render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="old" />);
    rerender(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="new" />);
    await screen.findByRole('button', { name: 'new.txt' });
    resolveOld(manifest(['old.txt']));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'old.txt' })).toBeNull());
    expect(gateway.readSourceControlOutputCaptureFile)
      .toHaveBeenLastCalledWith('token', 'p1', 'r1', 's1', null, 'new', 'new.txt');
  });

  it('surfaces file read errors from the retained viewer', async () => {
    gateway.readSourceControlOutputCapture.mockResolvedValue(manifest(['README.txt']));
    gateway.readSourceControlOutputCaptureFile.mockRejectedValue(new Error('File read failed'));
    render(<ProducedFilesPanel projectId="p1" runId="r1" sessionId="s1" captureId="capture-1" />);
    expect(await screen.findByText('File read failed')).toBeTruthy();
  });
});

describe('decodeCapturedFile', () => {
  it('decodes UTF-8 text and flags binary bytes', () => {
    expect(decodeCapturedFile('a.txt', new TextEncoder().encode('hi'))).toMatchObject({ content: 'hi', is_binary: false });
    expect(decodeCapturedFile('a.bin', new Uint8Array([0, 1]))).toMatchObject({ content: null, is_binary: true });
    expect(decodeCapturedFile('b.bin', new Uint8Array([0xff, 0xfe]))).toMatchObject({ content: null, is_binary: true });
  });

  describe('splitCapturePatch', () => {
    it('retains names and content for multiple files including deletions', () => {
      const patch = 'diff --git a/first.ts b/first.ts\n--- a/first.ts\n+++ b/first.ts\n@@ -1 +1 @@\n-old\n+new\n'
        + 'diff --git a/removed.ts b/removed.ts\n--- a/removed.ts\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n';
      expect(splitCapturePatch(patch).map((section) => section.filename)).toEqual(['first.ts', 'removed.ts']);
      expect(splitCapturePatch(patch).map((section) => section.patch).join('')).toBe(patch);
    });

    it('keeps a file header for rename-only and binary sections', () => {
      expect(splitCapturePatch('diff --git a/old b/new\nrename from old\nrename to new\n')[0].filename)
        .toBe('diff --git a/old b/new');
      expect(splitCapturePatch('diff --git a/image.png b/image.png\nBinary files differ\n')[0].filename)
        .toContain('image.png');
    });
  });
});
