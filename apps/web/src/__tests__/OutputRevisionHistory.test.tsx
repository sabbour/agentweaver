import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { apiClient } from '../api/apiClient';
import { OutputRevisionHistory } from '../components/OutputRevisionHistory';
import type { OutputRevision } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getOutputRevisionHistory: vi.fn(),
    getOutputRevision: vi.fn(),
    getOutputRevisionFile: vi.fn(),
    compareOutputRevisions: vi.fn(),
  },
}));

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

it('loads exact historic files and compares a predecessor without following the current branch', async () => {
  const revision: OutputRevision = {
    revision_id: 'new', lifecycle_generation: 2, predecessor_revision_id: 'old',
    tree_hash: 'tree', workflow_digest: 'workflow', diff_sha256: 'digest',
    diff: 'historic diff', files: [{ path: 'file.txt', size: 3, mode: 33188, sha256: 'abc' }],
    accepted_no_change: false, schema_version: 2, manifest_incomplete: false,
    tree_content_sha256: 'hash', output_kind: 'collective', merged_commit_hash: 'commit',
    created_at: '2026-09-28T00:00:00Z',
  };
  const previous: OutputRevision = {
    ...revision,
    revision_id: 'old',
    predecessor_revision_id: null,
    created_at: '2026-09-27T00:00:00Z',
  };
  vi.mocked(apiClient.getOutputRevisionHistory).mockResolvedValue([revision, previous]);
  vi.mocked(apiClient.getOutputRevision).mockResolvedValue(revision);
  vi.mocked(apiClient.compareOutputRevisions).mockResolvedValue({
    before_revision_id: 'old', after_revision_id: 'new',
    changes: [{ path: 'file.txt', before_sha256: null, after_sha256: 'abc' }],
  });
  vi.mocked(apiClient.getOutputRevisionFile).mockResolvedValue({
    revision_id: 'new', path: 'file.txt', mode: 33188, sha256: 'abc', content_base64: 'AQID',
  });

  render(<OutputRevisionHistory runId="run" currentReviewRevisionId="new" />);
  expect(await screen.findByRole('button', { name: 'Output version 1' })).toBeTruthy();
  fireEvent.click(await screen.findByRole('button', { name: 'Output version 2 · Under review' }));
  expect(await screen.findByRole('heading', { name: 'Output version 2' })).toBeTruthy();
  expect(screen.getByText('new')).toBeTruthy();
  await screen.findByText('Changed since old');
  fireEvent.click(screen.getByRole('button', { name: 'file.txt (3 bytes)' }));
  await screen.findByText('AQID');
  expect(apiClient.compareOutputRevisions).toHaveBeenCalledWith('run', 'old', 'new');
  expect(apiClient.getOutputRevisionFile).toHaveBeenCalledWith('run', 'new', 'file.txt');
});

it('shows a typed unavailability error rather than an empty history', async () => {
  vi.mocked(apiClient.getOutputRevisionHistory).mockRejectedValue(new Error('missing_content'));
  render(<OutputRevisionHistory runId="run" />);
  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('missing_content'));
});
