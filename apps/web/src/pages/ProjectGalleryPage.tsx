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
import type { CreateProjectRequest, GitHubRepo, Project } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { BlueprintPicker, applyBlueprintToRequest, NO_BLUEPRINT, type BlueprintSelection } from '../components/BlueprintPicker';
import { API_URL } from '../config';

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

function CreateBlankDialog({ onCreated, dataDir }: { onCreated: (p: Project) => void; dataDir: string | null }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('blank', onCreated);
  const [folderName, setFolderName] = useState('');
  const canCreate = Boolean(d.name.trim() && d.workingDirectory.trim() && !d.saving);

  const handleFolderChange = (value: string) => {
    setFolderName(value);
    d.setWorkingDirectory(dataDir ? `${dataDir}/${value}` : value);
  };

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => { d.setOpen(s.open); if (!s.open) { d.reset(); setFolderName(''); } }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary">Create blank project</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Create blank project</DialogTitle>
          <DialogContent>
            <div className={styles.dialogFields}>
              <Field label="Name" required>
                <Input value={d.name} onChange={(_, v) => d.setName(v.value)} placeholder="My project" />
              </Field>
              <Field
                label="Repository folder"
                required
                hint={dataDir ? `Folder name inside ${dataDir}` : 'Absolute path to a git repository on the machine running the Agentweaver server'}
              >
                <Input
                  contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
                  value={folderName}
                  onChange={(_, v) => handleFolderChange(v.value)}
                  placeholder="my-repo"
                />
              </Field>
              <BlueprintPicker active={d.open} value={d.blueprint} onChange={d.setBlueprint} />
              {d.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{d.error}</MessageBarBody>
                </MessageBar>
              )}
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

function useGitHubRepos(open: boolean) {
  const [repos, setRepos] = useState<GitHubRepo[]>([]);
  const [fetched, setFetched] = useState(false);
  const [authRequired, setAuthRequired] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [prevOpen, setPrevOpen] = useState(open);

  // Derived-state pattern: reset `fetched` when the dialog opens so `loading` is
  // truthy until the fetch completes.  Called during render (not inside an effect)
  // so it avoids cascading-render concerns.
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) {
      setFetched(false);
      setAuthRequired(false);
      setError(null);
    }
  }

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setAuthRequired(false);
    setError(null);
    apiClient.listGitHubRepos()
      .then((data) => { if (!cancelled) { setRepos(data); setFetched(true); } })
      .catch((err: unknown) => {
        if (cancelled) return;
        // A 401 means the app has no valid GitHub token — surface a connect path
        // instead of a silent empty list. Other failures get a retryable error.
        if (err instanceof ApiError && err.status === 401) {
          setAuthRequired(true);
        } else {
          setError(
            err instanceof ApiError
              ? `Error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
        setRepos([]);
        setFetched(true);
      });
    return () => { cancelled = true; };
  }, [open, reloadKey]);

  const reload = () => {
    setFetched(false);
    setReloadKey((k) => k + 1);
  };

  return { repos, loading: open && !fetched, authRequired, error, reload };
}

function CreateFromGitHubDialog({ onCreated, dataDir }: { onCreated: (p: Project) => void; dataDir: string | null }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('github', onCreated);
  const { repos, loading: reposLoading, authRequired, error: reposError, reload: reloadRepos } = useGitHubRepos(d.open);
  const [repoFilter, setRepoFilter] = useState('');
  const [folderName, setFolderName] = useState('');
  const canCreate = Boolean(
    d.name.trim() && d.workingDirectory.trim() && d.sourceRepository.trim() && !d.saving,
  );

  const handleFolderChange = (value: string) => {
    setFolderName(value);
    d.setWorkingDirectory(dataDir ? `${dataDir}/${value}` : value);
  };

  const filteredRepos = repos.filter(r =>
    r.full_name?.toLowerCase().includes(repoFilter.toLowerCase()) ?? false
  );

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => { d.setOpen(s.open); if (!s.open) { d.reset(); setRepoFilter(''); setFolderName(''); } }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="secondary">Create from GitHub</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Create project from GitHub</DialogTitle>
          <DialogContent>
            <div className={styles.dialogFields}>
              <Field label="Name" required>
                <Input value={d.name} onChange={(_, v) => d.setName(v.value)} placeholder="My project" />
              </Field>
              <Field label="Source repository" required hint="Search GitHub, or type owner/repo manually">
                <Combobox
                  freeform
                  placeholder={reposLoading ? 'Loading repositories...' : 'Search or enter owner/repo'}
                  value={d.sourceRepository}
                  onInput={(e) => {
                    const val = (e.target as HTMLInputElement).value;
                    setRepoFilter(val);
                    d.setSourceRepository(val);
                  }}
                  onOptionSelect={(_, data) => {
                    d.setSourceRepository(data.optionValue ?? '');
                    setRepoFilter(data.optionValue ?? '');
                  }}
                  disabled={reposLoading}
                >
                  {filteredRepos.map((repo) => (
                    <Option key={repo.full_name ?? ''} value={repo.full_name ?? ''} text={repo.full_name ?? ''}>
                      <div>
                        <Text weight="semibold">{repo.full_name ?? '(unnamed)'}</Text>
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
              {authRequired && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    Connect your GitHub account to list repositories, or type owner/repo manually.
                  </MessageBarBody>
                  <MessageBarActions>
                    <Button
                      size="small"
                      onClick={() => { window.location.href = `${API_URL}/auth/github/authorize`; }}
                    >
                      Connect GitHub
                    </Button>
                  </MessageBarActions>
                </MessageBar>
              )}
              {reposError && (
                <MessageBar intent="error">
                  <MessageBarBody>Could not load repositories: {reposError}</MessageBarBody>
                  <MessageBarActions>
                    <Button size="small" onClick={reloadRepos}>Retry</Button>
                  </MessageBarActions>
                </MessageBar>
              )}
              <Field
                required
                hint={dataDir ? `Folder name inside ${dataDir}` : 'Absolute path to a git repository on the machine running the Agentweaver server'}
              >
                <Input
                  contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
                  value={folderName}
                  onChange={(_, v) => handleFolderChange(v.value)}
                  placeholder="my-repo"
                />
              </Field>
              <BlueprintPicker active={d.open} value={d.blueprint} onChange={d.setBlueprint} />
              {d.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{d.error}</MessageBarBody>
                </MessageBar>
              )}
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
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dataDir, setDataDir] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiClient.getServerInfo()
      .then((info) => { if (!cancelled) setDataDir(info.data_directory); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    let cancelled = false;
    apiClient.listProjects()
      .then((list) => { if (!cancelled) setProjects(list); })
      .catch((err) => {
        if (!cancelled) setError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const handleCreated = (project: Project) => {
    setProjects((prev) => [...prev, project]);
  };

  return (
    <div className={styles.root}>
      <PageHeader title="Projects" subtitle="Your Agentweaver projects." />

      {loading && <Spinner label="Loading projects" />}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && !error && projects.length === 0 && (
        <div className={styles.emptyState}>
          <Text>No projects yet. Create one to get started.</Text>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} />
          </div>
        </div>
      )}

      {!loading && projects.length > 0 && (
        <>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} />
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
