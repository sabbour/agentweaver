import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { type ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProject: vi.fn(),
    getProjectWorkspaceRefs: vi.fn(),
    getProjectWorkspace: vi.fn(),
    getProjectWorkspaceFileContent: vi.fn(),
    // FileViewerModal's default content fetcher — never reached (we pass getContent).
    getRunFileContent: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { WorkspacePage } from '../pages/WorkspacePage';
import type { Project, WorkspaceNode, WorkspaceRefsResponse, WorkspaceFileContent } from '../api/types';

function Wrapper({ children, initialEntry = '/projects/proj-1/workspace' }: { children: ReactNode; initialEntry?: string }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/projects/:projectId/workspace" element={children} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>
  );
}

const REFS: WorkspaceRefsResponse = {
  current_branch: 'main',
  refs: [
    { kind: 'base', branch: 'main', label: 'main' },
    { kind: 'worktree', branch: 'run/abc', label: 'Run abc', run_id: 'abc', run_status: 'running' },
    { kind: 'assembly', branch: 'agentweaver/integration/coord-1', label: 'Assembly coord-1', run_id: 'coord-1', run_status: 'merged' },
  ],
};

const BASE_NODES: WorkspaceNode[] = [
  { path: 'README.md', is_folder: false, status: null },
  { path: 'src', is_folder: true, status: null },
  { path: 'src/index.ts', is_folder: false, status: null },
];

const WORKTREE_NODES: WorkspaceNode[] = [
  { path: 'feature.ts', is_folder: false, status: null },
];

const PROJECT: Project = {
  project_id: 'proj-1',
  name: 'Agentweaver',
  origin: 'blank',
  source_repository: null,
  working_directory: 'C:\\repo',
  default_branch: 'main',
  owner: 'sabbour',
  default_provider: 'github-copilot',
  default_model_github_copilot: null,
  default_model_microsoft_foundry: null,
  available: true,
  state: 'active',
  created_at: '2026-06-23T00:00:00Z',
  updated_at: '2026-06-23T00:00:00Z',
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue(PROJECT);
  vi.mocked(apiClient.getProjectWorkspaceRefs).mockResolvedValue(REFS);
  vi.mocked(apiClient.getProjectWorkspace).mockImplementation(async (_id: string, ref?: string) =>
    ref === 'run/abc' ? WORKTREE_NODES : BASE_NODES,
  );
});

afterEach(() => cleanup());

describe('WorkspacePage', () => {
  it('uses the loaded project name in the breadcrumb', async () => {
    render(<Wrapper><WorkspacePage /></Wrapper>);

    await waitFor(() =>
      expect(screen.getByLabelText('Breadcrumb').textContent).toContain('Agentweaver'),
    );
  });

  it('renders the file tree from the workspace and shows the current branch', async () => {
    render(<Wrapper><WorkspacePage /></Wrapper>);

    // The base branch tree loads (default ref).
    await waitFor(() => expect(screen.getByTitle('README.md')).toBeDefined());
    expect(screen.getByTitle('src/index.ts')).toBeDefined();

    // Default ref is the base branch.
    await waitFor(() =>
      expect(apiClient.getProjectWorkspace).toHaveBeenCalledWith('proj-1', 'main'),
    );
    // The current branch is shown in the toolbar.
    expect(screen.getByLabelText('Current branch').textContent).toContain('main');
  });

  it('lists base + worktree refs and refetches the tree when the ref changes', async () => {
    render(<Wrapper><WorkspacePage /></Wrapper>);

    await waitFor(() => expect(screen.getByTitle('README.md')).toBeDefined());

    // Open the ref selector and switch to the worktree ref.
    fireEvent.click(screen.getByRole('combobox', { name: 'Branch or worktree' }));
    fireEvent.click(await screen.findByRole('option', { name: /Run abc/ }));

    // The tree refetches at the new ref and renders its files.
    await waitFor(() =>
      expect(apiClient.getProjectWorkspace).toHaveBeenCalledWith('proj-1', 'run/abc'),
    );
    await waitFor(() => expect(screen.getByTitle('feature.ts')).toBeDefined());
    // The previous base-branch file is gone.
    expect(screen.queryByTitle('README.md')).toBeNull();
  });

  it('selects a workspace ref from run/ref query params', async () => {
    render(
      <Wrapper initialEntry="/projects/proj-1/workspace?run=coord-1&ref=agentweaver%2Fintegration%2Fcoord-1">
        <WorkspacePage />
      </Wrapper>,
    );

    await waitFor(() =>
      expect(apiClient.getProjectWorkspace).toHaveBeenCalledWith('proj-1', 'agentweaver/integration/coord-1'),
    );
  });

  it('opens the viewer with syntax-highlighted content from the selected ref', async () => {
    const content: WorkspaceFileContent = {
      path: 'src/index.ts',
      content: 'export const answer = 42;\n',
      is_binary: false,
      language: 'typescript',
    };
    vi.mocked(apiClient.getProjectWorkspaceFileContent).mockResolvedValue(content);

    render(<Wrapper><WorkspacePage /></Wrapper>);

    await waitFor(() => expect(screen.getByTitle('src/index.ts')).toBeDefined());
    fireEvent.click(screen.getByTitle('src/index.ts'));

    // Content is fetched from the project workspace endpoint at the active ref...
    await waitFor(() =>
      expect(apiClient.getProjectWorkspaceFileContent).toHaveBeenCalledWith('proj-1', 'src/index.ts', 'main'),
    );
    // ...and rendered in the viewer.
    await waitFor(() => expect(document.body.textContent).toContain('export const answer = 42;'));
    // The run worktree content endpoint is never touched (read-only project browse).
    expect(apiClient.getRunFileContent).not.toHaveBeenCalled();
  });

  it('opens a markdown file inline in the rendered preview by default (no modal)', async () => {
    vi.mocked(apiClient.getProjectWorkspaceFileContent).mockResolvedValue({
      path: 'README.md',
      content: '# Hello World\n\nSome docs.',
      is_binary: false,
      language: 'markdown',
    });

    render(<Wrapper><WorkspacePage /></Wrapper>);

    await waitFor(() => expect(screen.getByTitle('README.md')).toBeDefined());
    fireEvent.click(screen.getByTitle('README.md'));

    // The markdown renders inline (the heading text appears as rendered HTML).
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Hello World' })).toBeDefined());
    // Source/Preview tabs are present, with Preview the default.
    expect(screen.getByRole('tab', { name: 'Preview' })).toBeDefined();
    expect(screen.getByRole('tab', { name: 'Source' })).toBeDefined();
    // The file opens inline — no Dialog/modal is mounted.
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('switches the inline panel content when a different file is selected', async () => {
    vi.mocked(apiClient.getProjectWorkspaceFileContent).mockImplementation(async (_id, path) =>
      path === 'README.md'
        ? { path, content: '# Hello World', is_binary: false, language: 'markdown' }
        : { path, content: 'const x = 1;', is_binary: false, language: 'typescript' },
    );

    render(<Wrapper><WorkspacePage /></Wrapper>);
    await waitFor(() => expect(screen.getByTitle('README.md')).toBeDefined());

    fireEvent.click(screen.getByTitle('README.md'));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Hello World' })).toBeDefined());

    fireEvent.click(screen.getByTitle('src/index.ts'));
    await waitFor(() => expect(document.body.textContent).toContain('const x = 1;'));
    // The previous markdown heading is gone after switching files.
    expect(screen.queryByRole('heading', { name: 'Hello World' })).toBeNull();
  });

  it('shows the empty state when no file is selected', async () => {
    render(<Wrapper><WorkspacePage /></Wrapper>);
    await waitFor(() => expect(screen.getByTitle('README.md')).toBeDefined());
    expect(screen.getByText('Select a file to view its contents.')).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
