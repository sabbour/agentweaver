import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';
import { FileViewerModal } from '../components/FileViewerModal';
import type { WorkspaceFileContent } from '../api/types';

// The worktree-backed apiClient.getRunFileContent is what 409s for coordinator runs (they own no
// worktree). The coordinator assembly review supplies a getContent override that reads from the
// integration branch instead — this test asserts the modal honours that override.
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getRunFileContent: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
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
});
