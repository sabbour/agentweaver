import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { ArtifactBrowser } from '../components/ArtifactBrowser';
import type { WorkspaceFileEntry, WorkspaceFileDiff } from '../api/types';

// Mock apiClient so no real HTTP calls are made.
// getRunFiles and getRunFileDiff correspond to the methods Tank added to
// ScaffolderApiClient for the artifact-browser feature.
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunFiles: vi.fn(),
    getRunFileDiff: vi.fn(),
    submitReview: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function makeEntry(overrides: Partial<WorkspaceFileEntry> = {}): WorkspaceFileEntry {
  return {
    path: 'src/app.ts',
    status: 'added',
    scope: 'committed',
    added_lines: 0,
    removed_lines: 0,
    ...overrides,
  };
}

function makeDiff(overrides: Partial<WorkspaceFileDiff> = {}): WorkspaceFileDiff {
  return {
    path: 'src/app.ts',
    diff: null,
    status: 'added',
    is_binary: false,
    ...overrides,
  };
}

// Typed references to the mocked methods for use in assertions.
// vi.mocked asserts the deep-mocked type so .mockResolvedValue is available.
const getRunFilesMock     = () => vi.mocked(apiClient.getRunFiles);
const getRunFileDiffMock  = () => vi.mocked(apiClient.getRunFileDiff);

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  // Explicit cleanup guarantees no DOM state leaks between tests,
  // regardless of whether vitest auto-cleanup is active.
  cleanup();
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// Test suite
// ---------------------------------------------------------------------------

describe('ArtifactBrowser', () => {
  // AB-01: empty file list renders "No changes" text (FR-034, FR-038).
  // The browser must be accessible even before the agent writes any files.
  it('renders "No changes" text when the file list is empty', async () => {
    getRunFilesMock().mockResolvedValue([]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-001" runStatus="pending" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText('No changes')).toBeDefined();
    });
  });

  // AB-02: file list with one added file renders the file name and an "added" status icon
  // (FR-034 — each file annotated with new / modified / deleted).
  it('renders file path and added badge for a single added file', async () => {
    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'src/new-feature.ts', status: 'added' }),
    ]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-002" runStatus="in_progress" />
      </Wrapper>,
    );

    // The tree shows the filename only (folder "src" is expanded by default).
    await waitFor(() => {
      expect(screen.getByText('new-feature.ts')).toBeDefined();
    });

    // The status icon has aria-label matching the status.
    const statusIcons = screen.getAllByLabelText('added');
    expect(statusIcons.length).toBeGreaterThanOrEqual(1);
  });

  // AB-03: mixed-status file list renders correct status icons for each entry
  // (FR-034 — all three annotation types: added, modified, deleted).
  it('renders correct status badges for added, modified, and deleted files', async () => {
    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'src/new.ts',     status: 'added' }),
      makeEntry({ path: 'src/changed.ts', status: 'modified' }),
      makeEntry({ path: 'src/removed.ts', status: 'deleted' }),
    ]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-003" runStatus="awaiting_review" />
      </Wrapper>,
    );

    // The tree shows filenames only; folder "src" is expanded by default.
    await waitFor(() => {
      expect(screen.getByText('new.ts')).toBeDefined();
      expect(screen.getByText('changed.ts')).toBeDefined();
      expect(screen.getByText('removed.ts')).toBeDefined();
    });

    // Each status icon must appear with its aria-label.
    expect(screen.getAllByLabelText('added').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByLabelText('modified').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByLabelText('deleted').length).toBeGreaterThanOrEqual(1);
  });

  // AB-04: selecting a file calls getRunFileDiff and renders the diff viewer
  // (FR-036 — readonly diff view with line-level annotation).
  it('calls getRunFileDiff and renders diff content when a file is selected', async () => {
    const fakeDiffText =
      '--- a/src/app.ts\n' +
      '+++ b/src/app.ts\n' +
      '@@ -1 +1 @@\n' +
      '-old line\n' +
      '+new line\n';

    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'src/app.ts', status: 'modified' }),
    ]);
    getRunFileDiffMock().mockResolvedValue(
      makeDiff({ path: 'src/app.ts', diff: fakeDiffText, status: 'modified', is_binary: false }),
    );

    const user = userEvent.setup();

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-004" runStatus="awaiting_review" />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('app.ts')).toBeDefined());
    await user.click(screen.getByText('app.ts'));

    await waitFor(() => {
      expect(getRunFileDiffMock()).toHaveBeenCalledWith('run-004', 'src/app.ts');
    });

    // The DiffViewer renders individual diff lines as spans.
    await waitFor(() => {
      // DiffViewer renders sign and content in separate table cells.
      expect(screen.getByText('new line')).toBeDefined();
      expect(screen.getByText('old line')).toBeDefined();
    });
  });

  // AB-05: selecting a file whose diff response has is_binary=true shows
  // "Binary file — diff not available" in the right panel (FR-036).
  it('shows binary file notice when the diff response is marked as binary', async () => {
    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'assets/image.png', status: 'added' }),
    ]);
    getRunFileDiffMock().mockResolvedValue(
      makeDiff({ path: 'assets/image.png', diff: null, status: 'added', is_binary: true }),
    );

    const user = userEvent.setup();

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-005" runStatus="awaiting_review" />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByText('image.png')).toBeDefined());
    await user.click(screen.getByText('image.png'));

    await waitFor(() => {
      expect(screen.getByText('Binary file — diff not available')).toBeDefined();
    });
  });

  // AB-06: pending run status — the component fetches the file list once on
  // mount but does NOT register a polling interval, since the run has not
  // started and live updates are not needed yet (FR-038).
  it('fetches file list once on mount for pending run without polling', async () => {
    getRunFilesMock().mockResolvedValue([]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-006" runStatus="pending" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText('No changes')).toBeDefined();
    });

    // Initial fetch must have been called exactly once — no repeated polling
    // calls from a setInterval for a non-live run.
    expect(getRunFilesMock()).toHaveBeenCalledTimes(1);
  });

  // AB-07: terminal run status "merged" — the component renders in readonly
  // historical mode and shows the artifact-completion notice (FR-040, SC-017).
  it('renders historical-mode notice for a merged terminal run', async () => {
    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'src/app.ts', status: 'modified' }),
    ]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-007" runStatus="merged" />
      </Wrapper>,
    );

    await waitFor(() => {
      // The component renders a MessageBar with this exact text for all
      // terminal statuses (merged, declined, merge_failed, failed).
      expect(
        screen.getByText('Showing the artifact state at run completion.'),
      ).toBeDefined();
    });
  });
});
