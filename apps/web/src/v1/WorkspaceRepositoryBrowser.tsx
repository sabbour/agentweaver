import {
  Button,
  Dropdown,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FilesTabPanel } from '../components/ArtifactBrowser';
import { FileViewer } from '../components/FileViewer';
import type { WorkspaceFileContent, WorkspaceNode } from '../api/types';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient, type AgentweaverGatewayClient } from './api';
import { repoAppBrokerClient, type IdentityBrokerRepoAppClient } from './repoApp';
import type {
  SourceControlWorkspaceFile,
  SourceControlWorkspaceRef,
  SourceControlWorkspaceRefsPage,
  SourceControlWorkspaceTree,
} from './contracts';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, minWidth: 0 },
  toolbar: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', minWidth: 0 },
  refPicker: { flex: '1 1 240px', minWidth: 0, maxWidth: '100%' },
  panels: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 320px) minmax(0, 1fr)',
    gap: tokens.spacingHorizontalM,
    minWidth: 0,
    minHeight: '360px',
    overflow: 'hidden',
    '@media (max-width: 720px)': {
      display: 'flex',
      flexDirection: 'column',
      minHeight: 0,
      overflow: 'visible',
    },
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    minHeight: 0,
    overflow: 'hidden',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 720px)': { minHeight: '260px' },
  },
  panelHeader: {
    flex: '0 0 auto',
    minWidth: 0,
    overflow: 'hidden',
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  panelBody: { flex: 1, minWidth: 0, minHeight: 0, overflow: 'auto' },
  viewerBody: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, overflow: 'hidden' },
  selectedPath: { overflowWrap: 'anywhere' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type RepositoryIdentity = { owner: string; name: string };
type BrowserGateway = Pick<
  AgentweaverGatewayClient,
  'listWorkspaceRepositoryRefs' | 'readWorkspaceRepositoryTree' | 'readWorkspaceRepositoryFile'
>;
type BrowserBroker = Pick<IdentityBrokerRepoAppClient, 'getRepositories' | 'createRepositorySelection'>;

interface RefState {
  scope: string;
  items: SourceControlWorkspaceRef[];
  nextCursor: string | null;
  loading: boolean;
  loadingMore: boolean;
  error: string | null;
}

interface TreeState {
  key: string;
  tree: SourceControlWorkspaceTree | null;
  loading: boolean;
  error: string | null;
}

function workspaceRefKey(ref: SourceControlWorkspaceRef): string {
  return `${ref.kind}:${ref.name}:${ref.commitSha.toLowerCase()}`;
}

function workspaceError(reason: unknown): string {
  if (reason instanceof GatewayError) {
    if (reason.status === 403) return 'You do not have permission to browse this configured repository.';
    if (reason.status === 404) return 'The configured repository, ref, or file is not available.';
    if (reason.status === 409) return 'The project repository connection changed. Reload the page and try again.';
    if (reason.status === 413) return 'This repository response is larger than the supported browsing limit.';
    if (reason.status === 429) return 'GitHub is rate limiting repository reads. Try again shortly.';
    if (reason.status >= 500) return 'The configured repository could not be read right now.';
  }
  return 'The configured repository could not be read right now.';
}

function sameRepository(actual: string, expected: RepositoryIdentity): boolean {
  return actual.toLowerCase() === `${expected.owner}/${expected.name}`.toLowerCase();
}

function validateRefsPage(page: SourceControlWorkspaceRefsPage, repository: RepositoryIdentity): void {
  if (!sameRepository(page.repository, repository) ||
      !Array.isArray(page.items) ||
      page.items.length > 100 ||
      page.items.some((item) => !/^[a-f0-9]{40}$/i.test(item.commitSha)) ||
      (page.nextCursor !== null && !/^\d{1,5}$/.test(page.nextCursor)))
    throw new Error('The repository returned an invalid refs page.');
}

function validateTree(tree: SourceControlWorkspaceTree, repository: RepositoryIdentity, commitSha: string): void {
  if (!sameRepository(tree.repository, repository) ||
      tree.commitSha.toLowerCase() !== commitSha.toLowerCase() ||
      tree.reference.toLowerCase() !== commitSha.toLowerCase() ||
      !Array.isArray(tree.items) ||
      tree.items.length > 25_000)
    throw new Error('The repository returned a tree for a different commit.');
}

function validateFile(file: SourceControlWorkspaceFile, repository: RepositoryIdentity, commitSha: string, path: string): void {
  if (!sameRepository(file.repository, repository) ||
      file.commitSha.toLowerCase() !== commitSha.toLowerCase() ||
      file.path !== path ||
      file.size < 0 ||
      file.size > 1024 * 1024 ||
      file.isBinary && file.content !== null)
    throw new Error('The repository returned file content for a different selection.');
}

export function WorkspaceRepositoryBrowser({
  projectId,
  repository,
  authMode,
  gateway = gatewayClient,
  brokerClient = repoAppBrokerClient,
}: {
  projectId: string;
  repository: RepositoryIdentity | null;
  authMode?: string | null;
  gateway?: BrowserGateway;
  brokerClient?: BrowserBroker;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const repositoryFullName = repository ? `${repository.owner}/${repository.name}` : '';
  const scope = JSON.stringify([projectId, repositoryFullName.toLowerCase(), authMode]);
  const scopeRef = useRef(scope);
  scopeRef.current = scope;
  const [refsState, setRefsState] = useState<RefState>({
    scope: '',
    items: [],
    nextCursor: null,
    loading: false,
    loadingMore: false,
    error: null,
  });
  const [selectedRefState, setSelectedRefState] = useState<{ scope: string; refKey: string } | null>(null);
  const [treeGeneration, setTreeGeneration] = useState(0);
  const [treeState, setTreeState] = useState<TreeState>({
    key: '',
    tree: null,
    loading: false,
    error: null,
  });
  const [selectedFile, setSelectedFile] = useState<{ key: string; path: string } | null>(null);

  const issueRepositorySelection = useCallback(async (): Promise<string> => {
    if (!repositoryFullName) throw new Error('No repository is configured.');
    const { repositories } = await brokerClient.getRepositories();
    const matches = repositories.filter((candidate) =>
      candidate.fullName.toLowerCase() === repositoryFullName.toLowerCase());
    if (matches.length !== 1)
      throw new Error('The configured repository is not available through the connected GitHub App.');
    const selected = matches[0];
    return (await brokerClient.createRepositorySelection(
      selected.installationId,
      selected.repositoryId,
    )).code;
  }, [brokerClient, repositoryFullName]);

  const requestRefs = useCallback(async (cursor?: string | null) => {
    if (!repository) throw new Error('No repository is configured.');
    const selectionCode = await issueRepositorySelection();
    const page = await apiCall((token, tenant) => gateway.listWorkspaceRepositoryRefs(
      token, projectId, selectionCode, cursor, tenant,
    ), null);
    validateRefsPage(page, repository);
    return page;
  }, [apiCall, gateway, issueRepositorySelection, projectId, repository]);

  useEffect(() => {
    if (!repository || authMode !== 'githubApp' || !projectId) return;
    let active = true;
    setRefsState({
      scope,
      items: [],
      nextCursor: null,
      loading: true,
      loadingMore: false,
      error: null,
    });
    void requestRefs().then((page) => {
      if (active && scopeRef.current === scope)
        setRefsState({ scope, items: page.items, nextCursor: page.nextCursor, loading: false, loadingMore: false, error: null });
    }).catch((reason: unknown) => {
      if (active && scopeRef.current === scope)
        setRefsState({ scope, items: [], nextCursor: null, loading: false, loadingMore: false, error: workspaceError(reason) });
    });
    return () => { active = false; };
  }, [authMode, projectId, requestRefs, scope, treeGeneration]);

  const refs = refsState.scope === scope ? refsState.items : [];
  const defaultRef = refs.find((item) => item.isDefault) ?? refs[0];
  const selectedRef = (selectedRefState?.scope === scope
    ? refs.find((item) => workspaceRefKey(item) === selectedRefState.refKey)
    : null) ?? defaultRef ?? null;
  const selectedCommitSha = selectedRef?.commitSha ?? refs[0]?.commitSha ?? null;
  const treeKey = JSON.stringify([scope, selectedCommitSha]);
  const tree = treeState.key === treeKey ? treeState.tree : null;
  const treeLoading = !!selectedCommitSha && (treeState.key !== treeKey || treeState.loading);
  const treeError = treeState.key === treeKey ? treeState.error : null;
  const files = useMemo<WorkspaceNode[]>(
    () => (tree?.items ?? [])
      .filter((item) => item.kind === 'directory' || item.kind === 'file')
      .map((item) => ({ path: item.path, is_folder: item.kind === 'directory', status: null })),
    [tree],
  );
  const unsupportedCount = tree?.items.filter((item) =>
    item.kind === 'symbolicLink' || item.kind === 'submodule' || item.kind === 'unsupported').length ?? 0;
  const visibleSelectedFile = selectedFile?.key === treeKey ? selectedFile.path : null;

  useEffect(() => {
    if (!selectedCommitSha || !repository || authMode !== 'githubApp') return;
    let active = true;
    setTreeState({ key: treeKey, tree: null, loading: true, error: null });
    void (async () => {
      try {
        const selectionCode = await issueRepositorySelection();
        const result = await apiCall((token, tenant) => gateway.readWorkspaceRepositoryTree(
          token, projectId, selectionCode, selectedCommitSha, tenant,
        ), null);
        validateTree(result, repository, selectedCommitSha);
        if (active && scopeRef.current === scope)
          setTreeState({ key: treeKey, tree: result, loading: false, error: null });
      } catch (reason) {
        if (active && scopeRef.current === scope)
          setTreeState({ key: treeKey, tree: null, loading: false, error: workspaceError(reason) });
      }
    })();
    return () => { active = false; };
  }, [apiCall, authMode, gateway, issueRepositorySelection, projectId, repository, scope, selectedCommitSha, treeGeneration, treeKey]);

  const loadMoreRefs = async () => {
    const cursor = refsState.scope === scope ? refsState.nextCursor : null;
    if (!cursor || refsState.loadingMore) return;
    setRefsState((current) => current.scope === scope ? { ...current, loadingMore: true } : current);
    try {
      const page = await requestRefs(cursor);
      if (scopeRef.current !== scope) return;
      setRefsState((current) => current.scope === scope
        ? {
          ...current,
          items: [...current.items, ...page.items],
          nextCursor: page.nextCursor,
          loadingMore: false,
          error: null,
        }
        : current);
    } catch (reason) {
      if (scopeRef.current === scope)
        setRefsState((current) => current.scope === scope
          ? { ...current, loadingMore: false, error: workspaceError(reason) }
          : current);
    }
  };

  const readFile = useCallback(async (_projectId: string, path: string): Promise<WorkspaceFileContent> => {
    if (!repository || !selectedCommitSha) throw new Error('Select a repository ref before opening a file.');
    try {
      const selectionCode = await issueRepositorySelection();
      const file = await apiCall((token, tenant) => gateway.readWorkspaceRepositoryFile(
        token, projectId, path, selectionCode, selectedCommitSha, tenant,
      ), null);
      validateFile(file, repository, selectedCommitSha, path);
      return { path: file.path, content: file.content, is_binary: file.isBinary, language: file.language };
    } catch (reason) {
      throw new Error(workspaceError(reason));
    }
  }, [apiCall, gateway, issueRepositorySelection, projectId, repository, selectedCommitSha]);

  if (!repository) return <Text>No configured repository to browse.</Text>;
  if (authMode !== 'githubApp') {
    return <Text className={styles.muted}>Repository browsing requires the configured GitHub App connection. This credential type does not provide repository browsing.</Text>;
  }

  const refsLoading = refsState.scope !== scope || refsState.loading;
  const refsError = refsState.scope === scope ? refsState.error : null;
  const selectedRefValue = selectedRef ? workspaceRefKey(selectedRef) : '';

  return (
    <div className={styles.root} aria-label="Configured repository browser">
      {refsError && <MessageBar intent="error"><MessageBarBody>{refsError}</MessageBarBody></MessageBar>}
      <div className={styles.toolbar}>
        {refsLoading
          ? <Spinner size="tiny" label="Loading repository refs" />
          : refsError
            ? <Button appearance="secondary" onClick={() => setTreeGeneration((value) => value + 1)}>Retry</Button>
            : refs.length === 0
              ? <Text>No branches or tags are available.</Text>
              : (
                <Dropdown
                  className={styles.refPicker}
                  aria-label="Repository ref"
                  value={selectedRef ? `${selectedRef.name} (${selectedRef.kind}${selectedRef.isDefault ? ', default' : ''})` : ''}
                  selectedOptions={selectedRefValue ? [selectedRefValue] : []}
                  onOptionSelect={(_, data) => {
                    const next = refs.find((item) => workspaceRefKey(item) === data.optionValue);
                    if (next) setSelectedRefState({ scope, refKey: workspaceRefKey(next) });
                  }}
                  disabled={refs.length === 0}
                >
                  {refs.map((item) => (
                    <Option
                      key={workspaceRefKey(item)}
                      value={workspaceRefKey(item)}
                      text={`${item.name} (${item.kind}${item.isDefault ? ', default' : ''})`}
                    >
                      {item.name} ({item.kind}{item.isDefault ? ', default' : ''})
                    </Option>
                  ))}
                </Dropdown>
              )}
        {refsState.scope === scope && refsState.nextCursor && (
          <Button appearance="secondary" disabled={refsState.loadingMore} onClick={() => void loadMoreRefs()}>
            {refsState.loadingMore ? 'Loading refs…' : 'Load more refs'}
          </Button>
        )}
      </div>
      {refs.length > 0 && (
        <div className={styles.panels}>
          <section className={styles.panel} aria-label="Repository files">
            <div className={styles.panelHeader}>
              <Text weight="semibold">Files — {selectedRef?.name}</Text>
            </div>
            <div className={styles.panelBody}>
              {treeError
                ? (
                  <>
                    <MessageBar intent="error"><MessageBarBody>{treeError}</MessageBarBody></MessageBar>
                    <Button appearance="secondary" onClick={() => setTreeGeneration((value) => value + 1)}>
                      Retry
                    </Button>
                  </>
                )
                : treeLoading
                  ? <Spinner size="tiny" label="Loading repository tree" />
                  : (
                    <>
                      {unsupportedCount > 0 && (
                        <Text className={styles.muted}>
                          {unsupportedCount} symbolic link, submodule, or unsupported {unsupportedCount === 1 ? 'entry is' : 'entries are'} not viewable.
                        </Text>
                      )}
                      <FilesTabPanel
                        workspaceFiles={files}
                        workspaceLoading={false}
                        workspaceError={null}
                        selectedPath={visibleSelectedFile}
                        onFileClick={(path) => setSelectedFile({ key: treeKey, path })}
                      />
                    </>
                  )}
            </div>
          </section>
          <section className={styles.panel} aria-label="Repository file viewer">
            <div className={styles.panelHeader}>
              <Text weight="semibold" className={styles.selectedPath}>{visibleSelectedFile ?? 'Viewer'}</Text>
            </div>
            <div className={styles.viewerBody}>
              {visibleSelectedFile && selectedCommitSha
                ? (
                  <FileViewer
                    key={`${projectId}:${repositoryFullName}:${selectedCommitSha}:${visibleSelectedFile}`}
                    runId={projectId}
                    filePath={visibleSelectedFile}
                    getContent={readFile}
                  />
                )
                : <Text className={styles.muted}>Select a file to view its contents.</Text>}
            </div>
          </section>
        </div>
      )}
    </div>
  );
}
