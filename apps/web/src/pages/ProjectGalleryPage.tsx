import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  CardHeader,
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
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { CreateProjectRequest, ModelSource, Project } from '../api/types';

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
  const [defaultProvider, setDefaultProvider] = useState<ModelSource | ''>('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reset = () => {
    setName('');
    setWorkingDirectory('');
    setSourceRepository('');
    setDefaultProvider('');
    setError(null);
    setSaving(false);
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
      if (defaultProvider) req.default_provider = defaultProvider;
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
    sourceRepository, setSourceRepository, defaultProvider, setDefaultProvider,
    saving, error, handleSubmit, reset,
  };
}

function CreateBlankDialog({ onCreated }: { onCreated: (p: Project) => void }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('blank', onCreated);

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => { d.setOpen(s.open); if (!s.open) d.reset(); }}>
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
              <Field label="Working directory" required>
                <Input value={d.workingDirectory} onChange={(_, v) => d.setWorkingDirectory(v.value)} placeholder="C:/projects/my-project" />
              </Field>
              <Field label="Default provider">
                <Select value={d.defaultProvider} onChange={(_, v) => d.setDefaultProvider(v.value as ModelSource | '')}>
                  <option value="">— use server default —</option>
                  <option value="github-copilot">GitHub Copilot</option>
                  <option value="microsoft-foundry">Microsoft Foundry</option>
                </Select>
              </Field>
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
              disabled={!d.name.trim() || !d.workingDirectory.trim() || d.saving}
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

function CreateFromGitHubDialog({ onCreated }: { onCreated: (p: Project) => void }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('github', onCreated);

  return (
    <Dialog open={d.open} onOpenChange={(_, s) => { d.setOpen(s.open); if (!s.open) d.reset(); }}>
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
              <Field label="Source repository (owner/repo)" required>
                <Input value={d.sourceRepository} onChange={(_, v) => d.setSourceRepository(v.value)} placeholder="owner/repo" />
              </Field>
              <Field label="Working directory" required>
                <Input value={d.workingDirectory} onChange={(_, v) => d.setWorkingDirectory(v.value)} placeholder="C:/projects/my-project" />
              </Field>
              <Field label="Default provider">
                <Select value={d.defaultProvider} onChange={(_, v) => d.setDefaultProvider(v.value as ModelSource | '')}>
                  <option value="">— use server default —</option>
                  <option value="github-copilot">GitHub Copilot</option>
                  <option value="microsoft-foundry">Microsoft Foundry</option>
                </Select>
              </Field>
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
              disabled={!d.name.trim() || !d.workingDirectory.trim() || !d.sourceRepository.trim() || d.saving}
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
        <Badge appearance="tint" color="informative">{project.origin}</Badge>
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
      <Title2>Projects</Title2>

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
            <CreateBlankDialog onCreated={handleCreated} />
            <CreateFromGitHubDialog onCreated={handleCreated} />
          </div>
        </div>
      )}

      {!loading && projects.length > 0 && (
        <>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} />
            <CreateFromGitHubDialog onCreated={handleCreated} />
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
