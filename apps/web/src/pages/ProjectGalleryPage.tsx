import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { CreateProjectRequest, GitHubAccount, GitHubRepo, Project } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { BlueprintPicker, applyBlueprintToRequest, NO_BLUEPRINT, type BlueprintSelection } from '../components/BlueprintPicker';
import { useProjectList } from '../hooks/useProjectList';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  toolbar: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))',
    gap: tokens.spacingVerticalM,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  cardMeta: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  cardDir: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    wordBreak: 'break-all',
  },
  cardActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalS,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    alignItems: 'flex-start',
    padding: `${tokens.spacingVerticalXXL} 0`,
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  dialogTwoCol: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXL,
    alignItems: 'flex-start',
    '@media (max-width: 680px)': {
      flexDirection: 'column',
    },
  },
  dialogLeftCol: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    flex: '0 0 auto',
    width: '280px',
    minWidth: '240px',
    '@media (max-width: 680px)': {
      width: '100%',
    },
  },
  dialogRightCol: {
    display: 'flex',
    flexDirection: 'column',
    flex: '1 1 auto',
    minWidth: '240px',
    maxHeight: '460px',
    overflowY: 'auto',
    paddingRight: tokens.spacingHorizontalXS,
  },
  accountOption: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  accountAvatar: {
    width: '20px',
    height: '20px',
    borderRadius: '50%',
    flexShrink: 0,
  },
});

function useCreateProjectDialog(origin: 'blank' | 'github', onCreated: (p: Project) => void) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [workingDirectory, setWorkingDirectory] = useState('');
  const [sourceRepository, setSourceRepository] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [blueprint, setBlueprint] = useState<BlueprintSelection>(NO_BLUEPRINT);

  const reset = () => {
    setName('');
    setWorkingDirectory('');
    setSourceRepository('');
    setError(null);
    setSaving(false);
    setBlueprint(NO_BLUEPRINT);
  };

  const handleSubmit = async () => {
    if (!name.trim() || !workingDirectory.trim()) return;
    if (origin === 'github' && !sourceRepository.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const req: CreateProjectRequest = {
        name: name.trim(),
        origin,
        working_directory: workingDirectory.trim(),
      };
      if (origin === 'github') req.source_repository = sourceRepository.trim();
      applyBlueprintToRequest(req, blueprint);
      const project = await apiClient.createProject(req);
      onCreated(project);
      setOpen(false);
      reset();
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
      );
    } finally {
      setSaving(false);
    }
  };

  return {
    open, setOpen, name, setName, workingDirectory, setWorkingDirectory,
    sourceRepository, setSourceRepository,
    saving, error, handleSubmit, reset,
    blueprint, setBlueprint,
  };
}

function slugify(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

function CreateBlankDialog({ onCreated, dataDir, workspaceAutoAssigned }: { onCreated: (p: Project) => void; dataDir: string | null; workspaceAutoAssigned: boolean }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('blank', onCreated);
  const [folderName, setFolderName] = useState('');
  const [folderEdited, setFolderEdited] = useState(false);
  const canCreate = Boolean(d.name.trim() && d.workingDirectory.trim() && !d.saving);

  const handleFolderChange = (value: string) => {
    setFolderName(value);
    d.setWorkingDirectory(dataDir ? `${dataDir}/${value}` : value);
  };

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => { d.setOpen(s.open); if (!s.open) { d.reset(); setFolderName(''); setFolderEdited(false); } }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary">Create blank project</Button>
      </DialogTrigger>
      <DialogSurface style={{ maxWidth: '860px' }}>
        <DialogBody>
          <DialogTitle>Create blank project</DialogTitle>
          <DialogContent>
            <div className={styles.dialogTwoCol}>
              <div className={styles.dialogLeftCol}>
                <Field label="Name" required>
                  <Input
                    value={d.name}
                    onChange={(_, v) => {
                      d.setName(v.value);
                      if (workspaceAutoAssigned) {
                        d.setWorkingDirectory(slugify(v.value));
                      } else if (!folderEdited) {
                        handleFolderChange(slugify(v.value));
                      }
                    }}
                    placeholder="My project"
                  />
                </Field>
                {!workspaceAutoAssigned && (
                  <Field
                    label="Repository folder"
                    required
                    hint={dataDir ? `Folder name inside ${dataDir}` : 'Absolute path to a git repository on the machine running the Agentweaver server'}
                  >
                    <Input
                      contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
                      value={folderName}
                      onChange={(_, v) => {
                        setFolderEdited(v.value !== '');
                        handleFolderChange(v.value);
                      }}
                      placeholder="my-repo"
                    />
                  </Field>
                )}
                {d.error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{d.error}</MessageBarBody>
                  </MessageBar>
                )}
              </div>
              <div className={styles.dialogRightCol}>
                <BlueprintPicker active={d.open} value={d.blueprint} onChange={d.setBlueprint} />
              </div>
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={d.saving}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="primary"
              disabled={!canCreate}
              onClick={() => void d.handleSubmit()}
            >
              {d.saving ? 'Creating' : 'Create'}
            </Button>
            {d.saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function useGitHubData(open: boolean) {
  const [accounts, setAccounts] = useState<GitHubAccount[]>([]);
  const [accountsLoading, setAccountsLoading] = useState(false);
  const [authRequired, setAuthRequired] = useState(false);
  const [accountsError, setAccountsError] = useState<string | null>(null);
  const [accountsKey, setAccountsKey] = useState(0);

  const [selectedAccount, setSelectedAccount] = useState<GitHubAccount | null>(null);
  const [repos, setRepos] = useState<GitHubRepo[]>([]);
  const [reposLoading, setReposLoading] = useState(false);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposKey, setReposKey] = useState(0);

  // Reset all state when the dialog (re-)opens.
  const [prevOpen, setPrevOpen] = useState(open);
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) {
      setAccounts([]);
      setAccountsLoading(false);
      setAuthRequired(false);
      setAccountsError(null);
      setSelectedAccount(null);
      setRepos([]);
      setReposLoading(false);
      setReposError(null);
    }
  }

  // Load accounts when dialog opens.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setAccountsLoading(true);
    setAuthRequired(false);
    setAccountsError(null);
    apiClient.listGitHubAccounts()
      .then((data) => {
        if (cancelled) return;
        setAccounts(data);
        setAccountsLoading(false);
        // Auto-select the authenticated user (first entry).
        if (data.length > 0) setSelectedAccount(data[0]);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setAccountsLoading(false);
        if (err instanceof ApiError && err.status === 401) {
          setAuthRequired(true);
        } else {
          setAccountsError(
            err instanceof ApiError
              ? `Error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      });
    return () => { cancelled = true; };
  }, [open, accountsKey]);

  // Load repos whenever the selected account changes.
  useEffect(() => {
    if (!selectedAccount) { setRepos([]); return; }
    let cancelled = false;
    setReposLoading(true);
    setReposError(null);
    apiClient.listGitHubRepos(selectedAccount.login)
      .then((data) => {
        if (!cancelled) { setRepos(data); setReposLoading(false); }
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setReposLoading(false);
        setReposError(
          err instanceof ApiError
            ? `Error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
        setRepos([]);
      });
    return () => { cancelled = true; };
  }, [selectedAccount, reposKey]);

  const changeAccount = (acc: GitHubAccount) => {
    setSelectedAccount(acc);
    setRepos([]);
    setReposError(null);
  };

  const reloadAccounts = () => setAccountsKey((k) => k + 1);
  const reloadRepos = () => setReposKey((k) => k + 1);

  return {
    accounts, accountsLoading, authRequired, accountsError,
    selectedAccount, changeAccount,
    repos, reposLoading, reposError,
    reloadAccounts, reloadRepos,
  };
}

function CreateFromGitHubDialog({ onCreated, dataDir, workspaceAutoAssigned }: { onCreated: (p: Project) => void; dataDir: string | null; workspaceAutoAssigned: boolean }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('github', onCreated);
  const {
    accounts, accountsLoading, authRequired, accountsError,
    selectedAccount, changeAccount,
    repos, reposLoading, reposError,
    reloadAccounts, reloadRepos,
  } = useGitHubData(d.open);
  const [repoFilter, setRepoFilter] = useState('');
  const [folderName, setFolderName] = useState('');
  const [folderEdited, setFolderEdited] = useState(false);

  const canCreate = Boolean(
    d.name.trim() && d.workingDirectory.trim() && d.sourceRepository.trim() && !d.saving,
  );

  const handleFolderChange = (value: string) => {
    setFolderName(value);
    d.setWorkingDirectory(dataDir ? `${dataDir}/${value}` : value);
  };

  const filteredRepos = repos.filter(r =>
    r.fullName?.toLowerCase().includes(repoFilter.toLowerCase()) ?? false
  );

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => {
      d.setOpen(s.open);
      if (!s.open) { d.reset(); setRepoFilter(''); setFolderName(''); setFolderEdited(false); }
    }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="secondary">Create from GitHub</Button>
      </DialogTrigger>
      <DialogSurface style={{ maxWidth: '860px' }}>
        <DialogBody>
          <DialogTitle>Create project from GitHub</DialogTitle>
          <DialogContent>
            <div className={styles.dialogTwoCol}>
              <div className={styles.dialogLeftCol}>
                <Field label="Name" required>
                  <Input
                    value={d.name}
                    onChange={(_, v) => {
                      d.setName(v.value);
                      if (workspaceAutoAssigned) {
                        d.setWorkingDirectory(slugify(v.value));
                      } else if (!folderEdited) {
                        handleFolderChange(slugify(v.value));
                      }
                    }}
                    placeholder="My project"
                  />
                </Field>

                {/* Stage 1: Organization / account picker (hidden when unauthenticated) */}
                {!authRequired && (
                  <Field label="Organization" required hint="GitHub account or organization that owns the repository">
                    <Combobox
                      aria-label="Organization"
                      placeholder={accountsLoading ? 'Loading accounts...' : 'Select an account...'}
                      value={selectedAccount ? (selectedAccount.name ?? selectedAccount.login) : ''}
                      selectedOptions={selectedAccount ? [selectedAccount.login] : []}
                      onOptionSelect={(_, data) => {
                        const acc = accounts.find(a => a.login === data.optionValue);
                        if (acc) {
                          changeAccount(acc);
                          d.setSourceRepository('');
                          setRepoFilter('');
                        }
                      }}
                      disabled={accountsLoading}
                    >
                      {accounts.map(acc => (
                        <Option key={acc.login} value={acc.login} text={acc.name ?? acc.login}>
                          <div className={styles.accountOption}>
                            <img src={acc.avatar_url} alt="" className={styles.accountAvatar} />
                            <span>{acc.name ?? acc.login}</span>
                            {acc.type === 'org' && <Badge size="small" appearance="outline">Org</Badge>}
                          </div>
                        </Option>
                      ))}
                    </Combobox>
                  </Field>
                )}

                {/* 401 — prompt user to connect */}
                {authRequired && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      Connect your GitHub account to list repositories, or type owner/repo manually.
                    </MessageBarBody>
                    <MessageBarActions>
                      <Button
                        size="small"
                        onClick={() => { window.location.href = '/auth/github/authorize'; }}
                      >
                        Connect GitHub
                      </Button>
                    </MessageBarActions>
                  </MessageBar>
                )}

                {/* Non-401 accounts error */}
                {accountsError && (
                  <MessageBar intent="error">
                    <MessageBarBody>Could not load accounts: {accountsError}</MessageBarBody>
                    <MessageBarActions>
                      <Button size="small" onClick={reloadAccounts}>Retry</Button>
                    </MessageBarActions>
                  </MessageBar>
                )}

                {/* Stage 2: Repository picker */}
                <Field label="Source repository" required hint="Search GitHub, or type owner/repo manually">
                  <Combobox
                    aria-label="Repository"
                    freeform
                    placeholder={
                      accountsLoading ? 'Loading...' :
                      !selectedAccount && !authRequired ? 'Select an account first' :
                      reposLoading ? 'Loading repositories...' :
                      'Search or enter owner/repo'
                    }
                    value={d.sourceRepository}
                    onInput={(e) => {
                      const val = (e.target as HTMLInputElement).value;
                      setRepoFilter(val);
                      d.setSourceRepository(val);
                    }}
                    onOptionSelect={(_, data) => {
                      const fullName = data.optionValue ?? '';
                      d.setSourceRepository(fullName);
                      setRepoFilter(fullName);
                      const slug = fullName.split('/')[1] ?? fullName;
                      if (workspaceAutoAssigned) {
                        if (slug) d.setWorkingDirectory(slugify(slug));
                      } else if (slug && !folderEdited) {
                        handleFolderChange(slugify(slug));
                      }
                      if (!d.name.trim() && slug) {
                        d.setName(slug.replace(/-/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()));
                      }
                    }}
                    disabled={accountsLoading}
                  >
                    {filteredRepos.map((repo) => (
                      <Option key={repo.fullName ?? ''} value={repo.fullName ?? ''} text={repo.fullName ?? ''}>
                        <div>
                          <Text weight="semibold">{repo.fullName ?? '(unnamed)'}</Text>
                          {repo.description && (
                            <Text size={200} style={{ display: 'block', color: 'inherit', opacity: 0.7 }}>
                              {repo.description}
                            </Text>
                          )}
                        </div>
                      </Option>
                    ))}
                  </Combobox>
                </Field>

                {/* Repos load error */}
                {reposError && (
                  <MessageBar intent="error">
                    <MessageBarBody>Could not load repositories: {reposError}</MessageBarBody>
                    <MessageBarActions>
                      <Button size="small" onClick={reloadRepos}>Retry</Button>
                    </MessageBarActions>
                  </MessageBar>
                )}

                {!workspaceAutoAssigned && (
                  <Field
                    label="Repository folder"
                    required
                    hint={dataDir ? `Folder name inside ${dataDir}` : 'Absolute path to a git repository on the machine running the Agentweaver server'}
                  >
                    <Input
                      contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
                      value={folderName}
                      onChange={(_, v) => {
                        setFolderEdited(v.value !== '');
                        handleFolderChange(v.value);
                      }}
                      placeholder="my-repo"
                    />
                  </Field>
                )}

                {d.error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{d.error}</MessageBarBody>
                  </MessageBar>
                )}
              </div>
              <div className={styles.dialogRightCol}>
                <BlueprintPicker active={d.open} value={d.blueprint} onChange={d.setBlueprint} />
              </div>
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={d.saving}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="primary"
              disabled={!canCreate}
              onClick={() => void d.handleSubmit()}
            >
              {d.saving ? 'Creating' : 'Create'}
            </Button>
            {d.saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function ProjectCard({ project, onOpen }: { project: Project; onOpen: () => void }) {
  const styles = useStyles();
  return (
    <Card className={styles.card}>
      <CardHeader
        header={<Title3>{project.name}</Title3>}
        action={
          <Badge
            appearance="filled"
            color={project.available ? 'success' : 'warning'}
          >
            {project.available ? 'Available' : 'Unavailable'}
          </Badge>
        }
      />
      <div className={styles.cardMeta}>
        {project.source_repository && (
          <Text size={200}>{project.source_repository}</Text>
        )}
        <Text className={styles.cardDir}>{project.working_directory}</Text>
      </div>
      <div className={styles.cardActions}>
        <Button appearance="primary" size="small" onClick={onOpen}>Open</Button>
      </div>
    </Card>
  );
}

export function ProjectGalleryPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { projects, loading, authError, loadError, errorMessage, appendProject } = useProjectList();
  const [dataDir, setDataDir] = useState<string | null>(null);
  const [workspaceAutoAssigned, setWorkspaceAutoAssigned] = useState(false);

  useEffect(() => {
    let cancelled = false;
    apiClient.getServerInfo()
      .then((info) => {
        if (!cancelled) {
          setDataDir(info.data_directory);
          setWorkspaceAutoAssigned(info.workspace_auto_assigned ?? false);
        }
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  const handleCreated = (project: Project) => {
    appendProject(project);
  };

  return (
    <div className={styles.root}>
      <PageHeader title="Projects" subtitle="Your Agentweaver projects." />

      {loading && <Spinner label="Loading projects" />}

      {!loading && authError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Sign in with GitHub to see your projects.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              size="small"
              onClick={() => { window.location.href = '/auth/github/authorize'; }}
            >
              Sign in with GitHub
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage ?? 'Failed to load projects.'}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && !loadError && !authError && projects.length === 0 && (
        <div className={styles.emptyState}>
          <Text>No projects yet. Create one to get started.</Text>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </div>
        </div>
      )}

      {!loading && projects.length > 0 && (
        <>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </div>
          <div className={styles.grid}>
            {projects.map((p) => (
              <ProjectCard
                key={p.project_id}
                project={p}
                onOpen={() => navigate(`/projects/${p.project_id}`)}
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
