import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { GatewayError, type AgentweaverGatewayClient } from './api';
import type { IdentityBrokerRepoAppClient } from './repoApp';
import type {
  SourceControlWorkspaceFile,
  SourceControlWorkspaceRefsPage,
  SourceControlWorkspaceTree,
} from './contracts';
import { WorkspaceRepositoryBrowser } from './WorkspaceRepositoryBrowser';

const auth = vi.hoisted(() => ({
  apiCall: (operation: (token: string, tenantSelector: string | null) => Promise<unknown>) =>
    operation('browser-token', 'tenant-1'),
}));

vi.mock('./AuthContext', () => ({ useAuth: () => auth }));

type BrowserGateway = Pick<
  AgentweaverGatewayClient,
  'listWorkspaceRepositoryRefs' | 'readWorkspaceRepositoryTree' | 'readWorkspaceRepositoryFile'
>;
type BrowserBroker = Pick<IdentityBrokerRepoAppClient, 'getRepositories' | 'createRepositorySelection'>;

const MAIN_SHA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const RELEASE_SHA = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const repository = { owner: 'octo', name: 'repo' };

function refsPage(): SourceControlWorkspaceRefsPage {
  return {
    repository: 'octo/repo',
    defaultBranch: 'main',
    items: [
      { name: 'main', kind: 'branch', commitSha: MAIN_SHA, isDefault: true },
      { name: 'release', kind: 'branch', commitSha: RELEASE_SHA, isDefault: false },
      { name: 'v1.0', kind: 'tag', commitSha: RELEASE_SHA, isDefault: false },
    ],
    nextCursor: null,
  };
}

function tree(commitSha: string, paths: string[]): SourceControlWorkspaceTree {
  return {
    repository: 'octo/repo',
    reference: commitSha,
    commitSha,
    items: paths.map((path) => {
      const isDirectory = paths.some((candidate) => candidate.startsWith(`${path}/`));
      return {
        path,
        kind: isDirectory ? 'directory' : 'file',
        size: isDirectory ? null : 10,
        blobSha: isDirectory ? null : 'cccccccccccccccccccccccccccccccccccccccc',
      };
    }),
  };
}

function file(path: string, commitSha: string, content: string): SourceControlWorkspaceFile {
  return {
    repository: 'octo/repo',
    path,
    commitSha,
    content,
    isBinary: false,
    language: 'plaintext',
    size: content.length,
  };
}

function makeBrowserClients(
  overrides: {
    refs?: () => Promise<SourceControlWorkspaceRefsPage>;
    tree?: (commitSha: string) => Promise<SourceControlWorkspaceTree>;
    content?: (path: string, commitSha: string) => Promise<SourceControlWorkspaceFile>;
  } = {},
): { gateway: BrowserGateway; broker: BrowserBroker } {
  const refs = refsPage();
  const gateway: BrowserGateway = {
    listWorkspaceRepositoryRefs: vi.fn(() => overrides.refs?.() ?? Promise.resolve(refs)),
    readWorkspaceRepositoryTree: vi.fn((_token, _projectId, _selectionCode, commitSha) =>
      overrides.tree?.(commitSha) ?? Promise.resolve(tree(commitSha, ['src', 'src/nested', 'src/nested/readme.txt']))),
    readWorkspaceRepositoryFile: vi.fn((_token, _projectId, path, _selectionCode, commitSha) =>
      overrides.content?.(path, commitSha) ?? Promise.resolve(file(path, commitSha, 'repository text'))),
  };
  const broker: BrowserBroker = {
    getRepositories: vi.fn().mockResolvedValue({
      connectionId: 'connection-1',
      connectionRevision: 1,
      githubLogin: 'octo',
      repositories: [{
        installationId: 7,
        repositoryId: 11,
        fullName: 'octo/repo',
        ownerLogin: 'octo',
        isPrivate: true,
        defaultBranch: 'main',
      }],
    }),
    createRepositorySelection: vi.fn().mockResolvedValue({
      code: 'selection-code',
      connectionId: 'connection-1',
      connectionRevision: 1,
      installationId: 7,
      repositoryId: 11,
      repositoryFullName: 'octo/repo',
    }),
  };
  return { gateway, broker };
}

function renderBrowser(gateway: BrowserGateway, broker: BrowserBroker) {
  return render(
    <WorkspaceRepositoryBrowser
      projectId="p1"
      repository={repository}
      authMode="githubApp"
      gateway={gateway}
      brokerClient={broker}
    />,
  );
}

describe('WorkspaceRepositoryBrowser', () => {
  it('pins the tree and file to the selected commit and keeps same-commit refs distinct', async () => {
    const user = userEvent.setup();
    const { gateway, broker } = makeBrowserClients();
    renderBrowser(gateway, broker);

    const picker = await screen.findByRole('combobox', { name: 'Repository ref' });
    expect(await screen.findByRole('button', { name: 'nested' })).toBeTruthy();
    expect(gateway.readWorkspaceRepositoryTree).toHaveBeenCalledWith(
      'browser-token', 'p1', 'selection-code', MAIN_SHA, 'tenant-1',
    );

    await user.click(picker);
    await user.click(screen.getByRole('option', { name: 'v1.0 (tag)' }));
    await waitFor(() => expect((picker as HTMLInputElement).value).toBe('v1.0 (tag)'));
    const treeCommits = () => new Set(
      vi.mocked(gateway.readWorkspaceRepositoryTree).mock.calls.map((call) => call[3]),
    );
    expect(treeCommits()).toEqual(new Set([MAIN_SHA, RELEASE_SHA]));
    await user.click(picker);
    await user.click(screen.getByRole('option', { name: 'release (branch)' }));
    await waitFor(() => expect((picker as HTMLInputElement).value).toBe('release (branch)'));
    expect(treeCommits()).toEqual(new Set([MAIN_SHA, RELEASE_SHA]));

    const folder = screen.getByRole('button', { name: 'nested' });
    folder.focus();
    await user.keyboard('{Enter}');
    const fileButton = screen.getByRole('button', { name: 'readme.txt' });
    fileButton.focus();
    await user.keyboard('{Enter}');

    expect(await screen.findByText('repository text')).toBeTruthy();
    expect(gateway.readWorkspaceRepositoryFile).toHaveBeenCalledWith(
      'browser-token', 'p1', 'src/nested/readme.txt', 'selection-code', RELEASE_SHA, 'tenant-1',
    );
    expect(broker.createRepositorySelection).toHaveBeenCalled();

    folder.focus();
    await user.keyboard('{Enter}');
    expect(screen.queryByRole('button', { name: 'readme.txt' })).toBeNull();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('button', { name: 'readme.txt' })).toBeTruthy();
  });

  it('discards late tree and file responses after the selected commit changes', async () => {
    const user = userEvent.setup();
    let releaseFirstTree!: (value: SourceControlWorkspaceTree) => void;
    let resolveFileContent!: (value: SourceControlWorkspaceFile) => void;
    const firstTree = new Promise<SourceControlWorkspaceTree>((resolve) => { releaseFirstTree = resolve; });
    const pendingFile = new Promise<SourceControlWorkspaceFile>((resolve) => { resolveFileContent = resolve; });
    let mainTreeCalls = 0;
    const { gateway, broker } = makeBrowserClients({
      tree: (commitSha) => {
        if (commitSha === MAIN_SHA && mainTreeCalls++ === 0) return firstTree;
        return Promise.resolve(tree(commitSha, commitSha === MAIN_SHA
          ? ['main-only.txt']
          : ['src', 'src/nested', 'src/nested/release.txt']));
      },
      content: (path, commitSha) => commitSha === RELEASE_SHA
        ? pendingFile
        : Promise.resolve(file(path, commitSha, 'current main content')),
    });
    renderBrowser(gateway, broker);
    const picker = await screen.findByRole('combobox', { name: 'Repository ref' });

    await user.click(picker);
    await user.click(screen.getByRole('option', { name: 'release (branch)' }));
    const nested = await screen.findByRole('button', { name: 'nested' });
    await act(async () => { releaseFirstTree(tree(MAIN_SHA, ['stale-secret.txt'])); });
    expect(screen.queryByRole('button', { name: 'stale-secret.txt' })).toBeNull();

    nested.focus();
    await user.keyboard('{Enter}');
    const releaseFileButton = screen.getByRole('button', { name: 'release.txt' });
    releaseFileButton.focus();
    await user.keyboard('{Enter}');
    expect(await screen.findByLabelText('src/nested/release.txt')).toBeTruthy();

    await user.click(picker);
    await user.click(screen.getByRole('option', { name: 'main (branch, default)' }));
    expect(await screen.findByRole('button', { name: 'main-only.txt' })).toBeTruthy();
    await act(async () => {
      resolveFileContent(file('src/nested/release.txt', RELEASE_SHA, 'stale release content'));
    });
    expect(screen.queryByText('stale release content')).toBeNull();
    expect(screen.queryByLabelText('src/nested/release.txt')).toBeNull();
    expect(await screen.findByText('Select a file to view its contents.')).toBeTruthy();

  });

  it('shows explicit denied and empty refs states', async () => {
    const denied = makeBrowserClients({
      refs: () => Promise.reject(new GatewayError(403, { code: 'forbidden' }, 'Forbidden')),
    });
    const { unmount } = renderBrowser(denied.gateway, denied.broker);
    expect(await screen.findByText('You do not have permission to browse this configured repository.')).toBeTruthy();
    unmount();

    const empty = makeBrowserClients({ refs: async () => ({ ...refsPage(), items: [] }) });
    renderBrowser(empty.gateway, empty.broker);
    expect(await screen.findByText('No branches or tags are available.')).toBeTruthy();
    expect(empty.gateway.readWorkspaceRepositoryTree).not.toHaveBeenCalled();
  });

  it('shows refs and tree loading states and an actionable upstream error', async () => {
    const user = userEvent.setup();
    let resolveRefs!: (value: SourceControlWorkspaceRefsPage) => void;
    let rejectTree!: (reason: unknown) => void;
    const pendingRefs = new Promise<SourceControlWorkspaceRefsPage>((resolve) => { resolveRefs = resolve; });
    const pendingTree = new Promise<SourceControlWorkspaceTree>((_resolve, reject) => { rejectTree = reject; });
    const { gateway, broker } = makeBrowserClients({
      refs: () => pendingRefs,
      tree: () => pendingTree,
    });
    renderBrowser(gateway, broker);

    expect(await screen.findByRole('progressbar', { name: 'Loading repository refs' })).toBeTruthy();
    await act(async () => { resolveRefs(refsPage()); });
    expect(await screen.findByRole('progressbar', { name: 'Loading repository tree' })).toBeTruthy();
    await act(async () => {
      rejectTree(new GatewayError(503, { code: 'source_control_unavailable' }, 'Unavailable'));
    });
    expect(await screen.findByText('The configured repository could not be read right now.')).toBeTruthy();
    const refReadsBeforeRetry = vi.mocked(gateway.listWorkspaceRepositoryRefs).mock.calls.length;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(vi.mocked(gateway.listWorkspaceRepositoryRefs).mock.calls.length)
      .toBeGreaterThan(refReadsBeforeRetry));
    expect(await screen.findByText('The configured repository could not be read right now.')).toBeTruthy();
  });
});
