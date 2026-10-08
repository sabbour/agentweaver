import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { FileViewerModal } from '../components/FileViewerModal';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { WorkspaceFileContent } from '../api/types';
import type { ReactNode } from 'react';
// The worktree-backed apiClient.getRunFileContent is what 409s for coordinator runs (they own no
// worktree). The coordinator assembly review supplies a getContent override that reads from the
// integration branch instead — this test asserts the modal honours that override.
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunFileContent: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

describe('FileViewerModal — coordinator assembly content (Preview/Source, no worktree 409)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });
  afterEach(() => cleanup());

  it('fetches content via the assembly getContent override, never the worktree endpoint', async () => {
    const assemblyContent: WorkspaceFileContent = {
      path: 'src/feature.ts',
      content: 'export const answer = 42;\n',
      is_binary: false,
      language: 'typescript',
    };
    const getContent = vi.fn().mockResolvedValue(assemblyContent);

    render(
      <Wrapper>
        <FileViewerModal
          runId="coord-run-1"
          filePath="src/feature.ts"
          onClose={() => {}}
          diff={null}
          diffLoading={false}
          diffError={null}
          isChanged={false}
          getContent={getContent}
        />
      </Wrapper>,
    );

    // The coordinator override is used to resolve content from the integration branch...
    await waitFor(() => expect(getContent).toHaveBeenCalledWith('coord-run-1', 'src/feature.ts'));
    // ...and the worktree-backed endpoint (the 409 source) is never touched.
    expect(apiClient.getRunFileContent).not.toHaveBeenCalled();

    // The assembled file content renders (Preview works rather than surfacing "Worktree not available.").
    await waitFor(() => expect(document.body.textContent).toContain('export const answer = 42;'));
  });

  it('without an override falls back to the worktree-backed endpoint (normal runs unchanged)', async () => {
    vi.mocked(apiClient.getRunFileContent).mockResolvedValue({
      path: 'src/a.ts',
      content: 'const a = 1;\n',
      is_binary: false,
      language: 'typescript',
    });

    render(
      <Wrapper>
        <FileViewerModal
          runId="run-9"
          filePath="src/a.ts"
          onClose={() => {}}
          diff={null}
          diffLoading={false}
          diffError={null}
          isChanged={false}
        />
      </Wrapper>,
    );

    await waitFor(() => expect(apiClient.getRunFileContent).toHaveBeenCalledWith('run-9', 'src/a.ts'));
    await waitFor(() => expect(document.body.textContent).toContain('const a = 1;'));
  });

  it('opens changed markdown files on Preview by default', async () => {
    const getContent = vi.fn().mockResolvedValue({
      path: 'docs/guide.md',
      content: '# Preview first\n',
      is_binary: false,
      language: 'markdown',
    } satisfies WorkspaceFileContent);

    render(
      <Wrapper>
        <FileViewerModal
          runId="coord-run-md"
          filePath="docs/guide.md"
          onClose={() => {}}
          diff={{ path: 'docs/guide.md', diff: '@@ -1 +1 @@\n-Old\n+New\n', status: 'modified', is_binary: false }}
          diffLoading={false}
          diffError={null}
          isChanged
          getContent={getContent}
        />
      </Wrapper>,
    );

    await waitFor(() => expect(getContent).toHaveBeenCalledWith('coord-run-md', 'docs/guide.md'));
    await waitFor(() => expect(screen.getByRole('tab', { name: 'Preview' }).getAttribute('aria-selected')).toBe('true'));
    expect(screen.getByRole('tab', { name: 'Diff' }).getAttribute('aria-selected')).toBe('false');
    await waitFor(() => expect(document.body.textContent).toContain('Preview first'));
  });

  it('also opens changed .markdown (long extension) files on Preview by default', async () => {
    // ArtifactBrowser already treats .md and .markdown alike for icon/kind purposes; the
    // FileViewer's own markdown detection must match so a `.markdown` file isn't silently
    // stuck defaulting to Diff/Source.
    const getContent = vi.fn().mockResolvedValue({
      path: 'docs/guide.markdown',
      content: '# Preview first\n',
      is_binary: false,
      language: 'markdown',
    } satisfies WorkspaceFileContent);

    render(
      <Wrapper>
        <FileViewerModal
          runId="coord-run-md2"
          filePath="docs/guide.markdown"
          onClose={() => {}}
          diff={{ path: 'docs/guide.markdown', diff: '@@ -1 +1 @@\n-Old\n+New\n', status: 'modified', is_binary: false }}
          diffLoading={false}
          diffError={null}
          isChanged
          getContent={getContent}
        />
      </Wrapper>,
    );

    await waitFor(() => expect(getContent).toHaveBeenCalledWith('coord-run-md2', 'docs/guide.markdown'));
    await waitFor(() => expect(screen.getByRole('tab', { name: 'Preview' }).getAttribute('aria-selected')).toBe('true'));
    expect(screen.getByRole('tab', { name: 'Diff' }).getAttribute('aria-selected')).toBe('false');
    await waitFor(() => expect(document.body.textContent).toContain('Preview first'));
  });
});
