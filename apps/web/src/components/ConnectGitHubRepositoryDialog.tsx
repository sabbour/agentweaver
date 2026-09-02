import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage, isGitHubRepoAppConnectionRequired } from '../api/errors';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  Link as FluentLink,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Tab,
  TabList,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useEffect, useMemo, useState } from 'react';
import { useLocation } from 'react-router-dom';
import type { GitHubRepositorySelectionCandidate, RepositoryOwner } from '../api/types';

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  tabPanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  helperText: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
});

type DialogMode = 'create' | 'existing';

/** Derives the same kebab-case default repo name the backend falls back to, e.g. "My Project" -> "my-project". */
function slugify(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/(^-+)|(-+$)/g, '') || 'project';
}

function formatError(err: unknown): string {
  return formatApiErrorMessage(err, 'Could not connect the GitHub repository.');
}

interface ConnectGitHubRepositoryDialogProps {
  projectId: string;
  projectName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called with the connected repo's "owner/repo" once the connection succeeds. */
  onConnected: (sourceRepository: string, htmlUrl: string) => void;
}

/**
 * Shared connect/create-repository flow for a currently-unconnected (Blank-origin) project — reused
 * by both Project Settings and the more prominent dashboard banner. It supports either creating a
 * brand-new repository through the caller's Repo App authorization or selecting an existing,
 * Repo-App-authorized repository and attaching the project's current history to it.
 */
export function ConnectGitHubRepositoryDialog({
  projectId,
  projectName,
  open,
  onOpenChange,
  onConnected,
}: ConnectGitHubRepositoryDialogProps) {
  const styles = useStyles();
  const location = useLocation();
  const [mode, setMode] = useState<DialogMode>('create');
  const [owners, setOwners] = useState<RepositoryOwner[]>([]);
  const [ownersLoading, setOwnersLoading] = useState(false);
  const [ownersError, setOwnersError] = useState<string | null>(null);
  const [ownersConnectionRequired, setOwnersConnectionRequired] = useState(false);
  const [repos, setRepos] = useState<GitHubRepositorySelectionCandidate[]>([]);
  const [reposLoading, setReposLoading] = useState(false);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposConnectionRequired, setReposConnectionRequired] = useState(false);
  const [connectingRepoApp, setConnectingRepoApp] = useState(false);
  const [owner, setOwner] = useState('');
  const [name, setName] = useState('');
  const [isPrivate, setIsPrivate] = useState(true);
  const [repoFilter, setRepoFilter] = useState('');
  const [selectedRepository, setSelectedRepository] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<{ sourceRepository: string; htmlUrl: string } | null>(null);
  const [ownersReloadKey, setOwnersReloadKey] = useState(0);
  const [reposReloadKey, setReposReloadKey] = useState(0);

  const defaultName = slugify(projectName);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setOwnersLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setOwnersError(null);
    setOwnersConnectionRequired(false);
    void apiClient.listProjectRepositoryOwners(projectId)
      .then((list) => {
        if (cancelled) return;
        setOwners(list);
        if (list.length > 0) setOwner((prev) => prev || list[0].login);
      })
      .catch((err) => {
        if (cancelled) return;
        setOwnersConnectionRequired(isGitHubRepoAppConnectionRequired(err));
        setOwnersError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setOwnersLoading(false);
      });
    return () => { cancelled = true; };
  }, [open, projectId, ownersReloadKey]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setReposLoading(true); // eslint-disable-line react-hooks/set-state-in-effect
    setReposError(null);
    setReposConnectionRequired(false);
    void apiClient.listGitHubRepositorySelections()
      .then((list) => {
        if (cancelled) return;
        setRepos(list.repositories);
        if (list.repositories.length > 0) {
          setSelectedRepository((prev) => prev || list.repositories[0].full_name);
        }
      })
      .catch((err) => {
        if (cancelled) return;
        setReposConnectionRequired(isGitHubRepoAppConnectionRequired(err));
        setReposError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setReposLoading(false);
      });
    return () => { cancelled = true; };
  }, [open, reposReloadKey]);

  const filteredRepos = useMemo(() => {
    const filter = repoFilter.trim().toLowerCase();
    if (!filter) return repos;
    return repos.filter((repository) => repository.full_name.toLowerCase().includes(filter));
  }, [repoFilter, repos]);

  const effectiveSelectedRepository = filteredRepos.some((repository) => repository.full_name === selectedRepository)
    ? selectedRepository
    : (filteredRepos[0]?.full_name ?? '');

  const connectRepoApp = async () => {
    setConnectingRepoApp(true);
    try {
      const handoff = await apiClient.beginRepoAppAuthorization(`${location.pathname}${location.search}`);
      window.location.assign(handoff.authorization_url);
    } catch {
      setConnectingRepoApp(false);
    }
  };

  const handleCreate = async () => {
    if (!owner) return;
    setSaving(true);
    setError(null);
    try {
      const connected = await apiClient.createProjectRepository(projectId, {
        owner,
        name: name.trim() || undefined,
        private: isPrivate,
      });
      setResult({ sourceRepository: connected.source_repository, htmlUrl: connected.html_url });
      onConnected(connected.source_repository, connected.html_url);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSaving(false);
    }
  };

  const handleConnectExisting = async () => {
    if (!effectiveSelectedRepository) return;
    setSaving(true);
    setError(null);
    try {
      const issued = await apiClient.issueGitHubRepositorySelection(effectiveSelectedRepository);
      const connected = await apiClient.connectProjectRepository(projectId, {
        repository_selection_code: issued.selection_code,
      });
      setResult({ sourceRepository: connected.source_repository, htmlUrl: connected.html_url });
      onConnected(connected.source_repository, connected.html_url);
    } catch (err) {
      setError(formatError(err));
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    onOpenChange(false);
    setMode('create');
    setName('');
    setRepoFilter('');
    setResult(null);
    setError(null);
  };

  const renderRepoAppMessage = (
    message: string,
    connectionRequired: boolean,
    onRetry: () => void,
    testId: string,
  ) => (
    <MessageBar
      intent={connectionRequired ? 'warning' : 'error'}
      data-testid={testId}
      data-intent={connectionRequired ? 'warning' : 'error'}
    >
      <MessageBarBody>{message}</MessageBarBody>
      <MessageBarActions>
        {connectionRequired
          ? (
            <Button size="small" appearance="primary" disabled={connectingRepoApp} onClick={() => void connectRepoApp()}>
              {connectingRepoApp ? 'Opening GitHub…' : 'Connect GitHub'}
            </Button>
          )
          : <Button size="small" onClick={onRetry}>Retry</Button>}
      </MessageBarActions>
    </MessageBar>
  );

  const selectedRepoMeta = repos.find((repository) => repository.full_name === effectiveSelectedRepository) ?? null;

  return (
    <Dialog open={open} onOpenChange={(_, data) => onOpenChange(data.open)}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle
            action={
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
              </DialogTrigger>
            }
          >
            Connect a GitHub repository
          </DialogTitle>
          <DialogContent className={styles.stack}>
            {result ? (
              <MessageBar intent="success">
                <MessageBarBody>
                  Connected to{' '}
                  <FluentLink href={result.htmlUrl} target="_blank" rel="noreferrer">
                    {result.sourceRepository}
                  </FluentLink>
                  . The project's existing history has been pushed to it.
                </MessageBarBody>
              </MessageBar>
            ) : (
              <>
                <TabList selectedValue={mode} onTabSelect={(_, data) => setMode(data.value as DialogMode)}>
                  <Tab value="create">Create new repository</Tab>
                  <Tab value="existing">Connect existing repository</Tab>
                </TabList>

                {mode === 'create' ? (
                  <div className={styles.tabPanel}>
                    <div className={styles.helperText}>
                      Create a brand-new repository under one of your available GitHub owners.
                    </div>
                    {ownersLoading && <Spinner label="Loading GitHub accounts" />}
                    {ownersError && renderRepoAppMessage(
                      ownersError,
                      ownersConnectionRequired,
                      () => setOwnersReloadKey((k) => k + 1),
                      'connect-github-repository-owners-error',
                    )}
                    {!ownersLoading && !ownersError && (
                      <>
                        <Field label="Owner">
                          <Select value={owner} onChange={(_, data) => setOwner(data.value)}>
                            {owners.map((candidateOwner) => (
                              <option key={candidateOwner.login} value={candidateOwner.login}>
                                {candidateOwner.login} {candidateOwner.type === 'org' ? '(organization)' : '(you)'}
                              </option>
                            ))}
                          </Select>
                        </Field>
                        <Field label="Repository name">
                          <Input
                            value={name}
                            placeholder={defaultName}
                            onChange={(_, data) => setName(data.value)}
                          />
                        </Field>
                        <Switch
                          checked={isPrivate}
                          onChange={(_, data) => setIsPrivate(data.checked)}
                          label="Private repository"
                        />
                      </>
                    )}
                  </div>
                ) : (
                  <div className={styles.tabPanel}>
                    <div className={styles.helperText}>
                      Pick one of your Repo App-authorized repositories to attach it to this project.
                    </div>
                    {reposLoading && <Spinner label="Loading GitHub repositories" />}
                    {reposError && renderRepoAppMessage(
                      reposError,
                      reposConnectionRequired,
                      () => setReposReloadKey((k) => k + 1),
                      'connect-github-existing-repository-error',
                    )}
                    {!reposLoading && !reposError && (
                      <>
                        <Field label="Find repository">
                          <Input
                            value={repoFilter}
                            placeholder="Filter by owner or repository name"
                            onChange={(_, data) => setRepoFilter(data.value)}
                          />
                        </Field>
                        <Field label="Repository">
                          <Select
                            aria-label="Repository"
                            value={effectiveSelectedRepository}
                            onChange={(_, data) => setSelectedRepository(data.value)}
                          >
                            {filteredRepos.map((repository) => (
                              <option key={repository.full_name} value={repository.full_name}>
                                {repository.full_name}
                              </option>
                            ))}
                          </Select>
                        </Field>
                        {filteredRepos.length === 0 && (
                          <MessageBar intent="info">
                            <MessageBarBody>No repositories match that filter.</MessageBarBody>
                          </MessageBar>
                        )}
                        {selectedRepoMeta && (
                          <div className={styles.helperText}>
                            {selectedRepoMeta.private ? 'Private' : 'Public'} repo · default branch {selectedRepoMeta.default_branch}
                          </div>
                        )}
                      </>
                    )}
                  </div>
                )}

                {error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{error}</MessageBarBody>
                  </MessageBar>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={handleClose}>
              {result ? 'Close' : 'Cancel'}
            </Button>
            {!result && (
              mode === 'create'
                ? (
                  <Button
                    appearance="primary"
                    disabled={!owner || saving || ownersLoading || Boolean(ownersError)}
                    onClick={() => void handleCreate()}
                    style={{ whiteSpace: 'nowrap' }}
                  >
                    {saving ? <Spinner size="tiny" /> : 'Create repository'}
                  </Button>
                )
                : (
                  <Button
                    appearance="primary"
                    disabled={!effectiveSelectedRepository || saving || reposLoading || Boolean(reposError)}
                    onClick={() => void handleConnectExisting()}
                    style={{ whiteSpace: 'nowrap' }}
                  >
                    {saving ? <Spinner size="tiny" /> : 'Connect repository'}
                  </Button>
                )
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
