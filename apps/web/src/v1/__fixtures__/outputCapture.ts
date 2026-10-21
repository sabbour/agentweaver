import type { SourceControlOutputCaptureDetail } from '../contracts';

/** Saved owner output shared by component and browser tests, never product data. */
export const capturedText = '# Release notes\n\nThe build is ready.\n';
export const capturedPath = 'docs/release notes.md';
export const captureDetail = (runId = 'r-1', captureId = 'capture-1'): SourceControlOutputCaptureDetail => ({
  capture: {
    captureId, eventId: '49dc5764-321d-49ec-96c2-d66b87476aa2', eventPosition: 1,
    capturedAt: '2026-10-20T12:00:00.000Z', workspaceId: 'workspace-1', branchName: 'release-notes',
    baseSha: 'a'.repeat(40), outputTreeSha: 'b'.repeat(40), manifestSha256: 'c'.repeat(64),
    manifestByteLength: 100, patchSha256: 'd'.repeat(64), patchByteLength: 40, packageByteLength: 200,
  },
  manifest: {
    contractVersion: 1, workspaceId: 'workspace-1', runId, repositoryId: 'repo-1', resourceGeneration: 1,
    workspaceIncarnationId: '67ea61dc-9046-4472-bb1c-077bd436d1cf', branchName: 'release-notes',
    baseSha: 'a'.repeat(40), outputTreeSha: 'b'.repeat(40),
    files: [{ path: capturedPath, mode: '100644', sha256: 'b8c1e6d8fa99c95daeb8fffbd9e8604d24fb913a8588202dfc861d037d88cd27',
      byteLength: new TextEncoder().encode(capturedText).byteLength }],
  },
});
