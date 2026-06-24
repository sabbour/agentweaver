import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { ArtifactBrowser, FilesTabPanel } from '../components/ArtifactBrowser';
import type { WorkspaceFileEntry, WorkspaceFileDiff, WorkspaceNode } from '../api/types';

// Mock apiClient so no real HTTP calls are made.
// getRunFiles and getRunFileDiff correspond to the methods Tank added to
// AgentweaverApiClient for the artifact-browser feature.
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunFiles: vi.fn(),
    getRunFileDiff: vi.fn(),
    submitReview: vi.fn(),
    commitRun: vi.fn(),
    requestChanges: vi.fn(),
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
const getRunFilesMock      = () => vi.mocked(apiClient.getRunFiles);
const getRunFileDiffMock   = () => vi.mocked(apiClient.getRunFileDiff);
const requestChangesMock   = () => vi.mocked(apiClient.requestChanges);
const commitRunMock        = () => vi.mocked(apiClient.commitRun);

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

  // AB-01b: at a review gate, an empty file list means the run reached review with zero
  // committed changes — surface a clear explanation, not a bare "No changes" label.
  it('explains "no changes produced" when the file list is empty at a review gate', async () => {
    getRunFilesMock().mockResolvedValue([]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-001b" runStatus="awaiting_review" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText('This run produced no changes to review.')).toBeDefined();
    });
    expect(
      screen.getByText(/written output outside the repository, or there was nothing to change/),
    ).toBeDefined();
  });

  // AB-01c: the explicit run.no_changes_produced signal forces the explanation and lists the
  // subtasks that produced nothing, regardless of run status.
  it('renders the no-changes explanation and subtasks when noChangesProduced is set', async () => {
    getRunFilesMock().mockResolvedValue([]);

    render(
      <Wrapper>
        <ArtifactBrowser
          runId="run-001c"
          runStatus="in_progress"
          noChangesProduced
          noChangeSubtaskIds={['Audit repo', 'Apply fixes']}
        />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText('This run produced no changes to review.')).toBeDefined();
    });
    expect(screen.getByText(/Subtasks with no changes: Audit repo, Apply fixes\./)).toBeDefined();
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

    // The unified three-button review bar is shown for awaiting_review.
    expect(screen.getByLabelText('Commit and merge to originating branch')).toBeDefined();
    expect(screen.getByLabelText('Request change')).toBeDefined();
    expect(screen.getByLabelText('Decline run')).toBeDefined();
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

    // With syntax highlighting enabled, tokens like "new" (a TS keyword) may be
    // split across multiple spans. Check the rendered text content instead.
    await waitFor(() => {
      const body = document.body.textContent ?? '';
      expect(body).toContain('new');
      expect(body).toContain('old');
      expect(body).toContain('line');
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

  // AB-08: Request change flow — clicking "Request change" reveals a textarea;
  // submitting calls apiClient.requestChanges with the comment text.
  it('calls requestChanges with the comment when the Request change flow is submitted', async () => {
    getRunFilesMock().mockResolvedValue([]);
    requestChangesMock().mockResolvedValue({ run_id: 'run-008', status: 'in_progress' });

    const user = userEvent.setup();

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-008" runStatus="awaiting_review" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText('Request change')).toBeDefined();
    });

    // Open the Request change box.
    await user.click(screen.getByLabelText('Request change'));

    // The textarea should now be visible.
    await waitFor(() => {
      expect(screen.getByLabelText('Changes requested comment')).toBeDefined();
    });

    // Send button is disabled when comment is empty.
    expect(screen.getByLabelText('Send change request to agent').hasAttribute('disabled') ||
      screen.getByLabelText('Send change request to agent').getAttribute('aria-disabled') === 'true').toBe(true);

    // Type a comment.
    await user.type(screen.getByLabelText('Changes requested comment'), 'Please fix the formatting');

    // Send button should now be enabled.
    await user.click(screen.getByLabelText('Send change request to agent'));

    await waitFor(() => {
      expect(requestChangesMock()).toHaveBeenCalledWith('run-008', 'Please fix the formatting');
    });
  });

  // AB-09: Review bar reappears on second review gate after request-changes.
  // Scenario: awaiting_review (bar shows) -> request-changes submitted
  // -> runStatus becomes in_progress (bar hidden) -> runStatus becomes
  // awaiting_review again (bar must show despite stale requestChangesResult).
  it('shows review bar again on second review gate after a request-changes cycle', async () => {    getRunFilesMock().mockResolvedValue([]);
    requestChangesMock().mockResolvedValue({ run_id: 'run-009', status: 'in_progress' });

    const user = userEvent.setup();

    const { rerender } = render(
      <Wrapper>
        <ArtifactBrowser runId="run-009" runStatus="awaiting_review" />
      </Wrapper>,
    );

    // Step 1: bar is visible at the first review gate.
    await waitFor(() => {
      expect(screen.getByLabelText('Commit and merge to originating branch')).toBeDefined();
    });

    // Step 2: submit a request-changes; this sets requestChangesResult internally.
    await user.click(screen.getByLabelText('Request change'));
    await waitFor(() => {
      expect(screen.getByLabelText('Changes requested comment')).toBeDefined();
    });
    await user.type(screen.getByLabelText('Changes requested comment'), 'Add more tests');
    await user.click(screen.getByLabelText('Send change request to agent'));
    await waitFor(() => {
      expect(requestChangesMock()).toHaveBeenCalledWith('run-009', 'Add more tests');
    });

    // Step 3: server starts revision; runStatus transitions to in_progress.
    rerender(
      <Wrapper>
        <ArtifactBrowser runId="run-009" runStatus="in_progress" />
      </Wrapper>,
    );
    // Bar must not be visible while the agent is revising.
    expect(screen.queryByLabelText('Commit and merge to originating branch')).toBeNull();

    // Step 4: second review gate arrives; runStatus returns to awaiting_review.
    rerender(
      <Wrapper>
        <ArtifactBrowser runId="run-009" runStatus="awaiting_review" />
      </Wrapper>,
    );
    // Bar must reappear even though requestChangesResult is still set.
    await waitFor(() => {
      expect(screen.getByLabelText('Commit and merge to originating branch')).toBeDefined();
      expect(screen.getByLabelText('Request change')).toBeDefined();
      expect(screen.getByLabelText('Decline run')).toBeDefined();
    });
  });

  // AB-10: Commit success triggers onCommitSuccess callback (reconnect hook for SSE).
  // After commitRun() resolves, the caller's onCommitSuccess must be invoked so
  // the SSE stream can reconnect and receive run.merged / run.completed events.
  it('calls onCommitSuccess after a successful commit', async () => {
    getRunFilesMock().mockResolvedValue([]);
    commitRunMock().mockResolvedValue({ run_id: 'run-010', status: 'merging', merge_result: null, conflicting_files: null });

    const onCommitSuccess = vi.fn();
    const user = userEvent.setup();

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-010" runStatus="awaiting_review" onCommitSuccess={onCommitSuccess} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByLabelText('Commit and merge to originating branch')).toBeDefined();
    });

    await user.click(screen.getByLabelText('Commit and merge to originating branch'));

    await waitFor(() => {
      expect(commitRunMock()).toHaveBeenCalledWith('run-010');
      expect(onCommitSuccess).toHaveBeenCalledTimes(1);
    });
  });

  // AB-11: Modified files in the Changes tab show an 'M' status badge (not 'D').
  // Ensures the amber/modified color path is taken, not the red/deleted path.
  it('modified files in Changes tab show M badge and modified aria-label', async () => {
    getRunFilesMock().mockResolvedValue([
      makeEntry({ path: 'src/modified.ts', status: 'modified', added_lines: 3, removed_lines: 1 }),
    ]);

    render(
      <Wrapper>
        <ArtifactBrowser runId="run-011" runStatus="in_progress" />
      </Wrapper>,
    );

    await waitFor(() => {
      // Status badge letter for modified must be 'M', not 'D' (deleted) or 'A' (added).
      expect(screen.getByText('M')).toBeDefined();
      expect(screen.queryByText('D')).toBeNull();
    });

    // The status icon must carry aria-label="modified".
    expect(screen.getAllByLabelText('modified').length).toBeGreaterThanOrEqual(1);
  });
});

// ---------------------------------------------------------------------------
// FilesTabPanel — extension-specific file icons (ws-file-icons)
// ---------------------------------------------------------------------------

describe('FilesTabPanel file icons', () => {
  function makeNode(overrides: Partial<WorkspaceNode> = {}): WorkspaceNode {
    return {
      path: 'README.md',
      is_folder: false,
      status: null,
      ...overrides,
    };
  }

  function renderPanel(files: WorkspaceNode[]) {
    return render(
      <Wrapper>
        <FilesTabPanel
          workspaceFiles={files}
          workspaceLoading={false}
          workspaceError={null}
          selectedPath={null}
          onFileClick={() => {}}
        />
      </Wrapper>,
    );
  }

  // FI-01: a markdown file gets the markdown/text icon.
  it('renders the markdown icon for a .md file', () => {
    const { container } = renderPanel([makeNode({ path: 'docs/guide.md' })]);
    expect(container.querySelector('[data-file-icon="markdown"]')).not.toBeNull();
  });

  // FI-02: a TypeScript file gets the code icon.
  it('renders the code icon for a .ts file', () => {
    const { container } = renderPanel([makeNode({ path: 'src/app.ts' })]);
    expect(container.querySelector('[data-file-icon="code"]')).not.toBeNull();
  });

  // FI-03: an unknown extension falls back to the neutral document icon.
  it('falls back to the document icon for an unknown extension', () => {
    const { container } = renderPanel([makeNode({ path: 'data/archive.xyz' })]);
    expect(container.querySelector('[data-file-icon="document"]')).not.toBeNull();
  });

  // FI-04: a changed file in the review/diff tree keeps its status coloring while still
  // using the extension icon glyph.
  it('preserves the status indication for a changed file and still uses the extension icon', () => {
    const { container } = renderPanel([
      makeNode({ path: 'src/changed.ts', status: 'modified' }),
    ]);
    const icon = container.querySelector('[data-file-icon="code"]');
    expect(icon).not.toBeNull();
    // The status icon span reflects the change status via its aria-label.
    expect(icon?.getAttribute('aria-label')).toBe('modified');
  });
});
