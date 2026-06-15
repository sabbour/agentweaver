import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Badge,
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
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { CreateProjectRunRequest, Project, ProjectRunSummary } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '860px',
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  infoGrid: {
    display: 'grid',
    gridTemplateColumns: '160px 1fr',
    gap: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    alignItems: 'start',
  },
  infoLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  infoValue: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    wordBreak: 'break-all',
  },
  runList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  runRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  runTask: {
    flex: 1,
    fontSize: tokens.fontSizeBase300,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  runMeta: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

function StartRunDialog({ projectId, onStarted }: { projectId: string; onStarted: (runId: string) => void }) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [task, setTask] = useState('');
  const [modelId, setModelId] = useState('');
  const [baseBranch, setBaseBranch] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reset = () => {
    setTask('');
    setModelId('');
    setBaseBranch('');
    setError(null);
    setSaving(false);
  };

  const handleSubmit = async () => {
    if (!task.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const req: CreateProjectRunRequest = { task: task.trim() };
      req.model_source = 'github-copilot';
      if (modelId.trim()) req.model_id = modelId.trim();
      if (baseBranch.trim()) req.base_branch = baseBranch.trim();
      const result = await apiClient.startProjectRun(projectId, req);
      setOpen(false);
      reset();
      onStarted(result.run_id);
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

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) reset(); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary">Start run</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Start a run</DialogTitle>
          <DialogContent>
            <div className={styles.dialogFields}>
              <Field label="Task" required>
                <Textarea
                  value={task}
                  onChange={(_, v) => setTask(v.value)}
                  placeholder="Describe what the agent should do..."
                  rows={4}
                />
              </Field>
              <Field label="Model ID (optional)">
                <Input value={modelId} onChange={(_, v) => setModelId(v.value)} placeholder="e.g. gpt-4o" />
              </Field>
              <Field label="Base branch (optional)">
                <Input value={baseBranch} onChange={(_, v) => setBaseBranch(v.value)} placeholder="e.g. main" />
              </Field>
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={saving}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="primary"
              disabled={!task.trim() || saving}
              onClick={() => void handleSubmit()}
            >
              {saving ? 'Starting' : 'Start'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function RunRow({ run, projectId }: { run: ProjectRunSummary; projectId: string }) {
  const styles = useStyles();
  return (
    <div className={styles.runRow}>
      <Badge appearance="tint" color={
        run.status === 'completed' || run.status === 'merged' ? 'success' :
        run.status === 'failed' || run.status === 'merge_failed' ? 'danger' :
        run.status === 'in_progress' ? 'informative' : 'subtle'
      }>
        {run.status}
      </Badge>
      <Text className={styles.runTask}>{run.task ?? '(no task description)'}</Text>
      <Text className={styles.runMeta}>{run.model_source}</Text>
      <Text className={styles.runMeta}>{new Date(run.started_at).toLocaleString()}</Text>
      <Link to={`/watch/${run.run_id}`} state={{ projectId }} style={{ textDecoration: 'none' }}>
        <Button appearance="secondary">Watch</Button>
      </Link>
    </div>
  );
}

export function ProjectPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [runs, setRuns] = useState<ProjectRunSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    Promise.all([
      apiClient.getProject(projectId),
      apiClient.listProjectRuns(projectId),
    ])
      .then(([proj, runList]) => {
        if (!cancelled) {
          setProject(proj);
          setRuns([...runList].reverse());
        }
      })
      .catch((err) => {
        if (!cancelled) setError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <span>{project?.name ?? projectId}</span>
      </div>

      {loading && <Spinner label="Loading project" />}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {project && !project.available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This project is unavailable. The working directory may have moved. Go to{' '}
            <Link to={`/projects/${projectId}/settings`}>settings</Link> to relink it.
          </MessageBarBody>
        </MessageBar>
      )}

      {project && (
        <>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <Title2>{project.name}</Title2>
            <div className={styles.actions}>
              <Link to={`/projects/${projectId}/settings`} style={{ textDecoration: 'none' }}>
                <Button appearance="secondary">Settings</Button>
              </Link>
              <Link to={`/projects/${projectId}/team`} style={{ textDecoration: 'none' }}>
                <Button appearance="secondary">Team</Button>
              </Link>
              <StartRunDialog
                projectId={projectId}
                onStarted={(runId) => navigate(`/watch/${runId}`, { state: { projectId } })}
              />
            </div>
          </div>

          <div className={styles.infoGrid}>
            <Text className={styles.infoLabel}>Origin</Text>
            <Badge appearance="tint" color="informative">{project.origin}</Badge>

            {project.source_repository && (
              <>
                <Text className={styles.infoLabel}>Repository</Text>
                <Text className={styles.infoValue}>{project.source_repository}</Text>
              </>
            )}

            <Text className={styles.infoLabel}>Working directory</Text>
            <Text className={styles.infoValue}>{project.working_directory}</Text>

            <Text className={styles.infoLabel}>Default branch</Text>
            <Text className={styles.infoValue}>{project.default_branch}</Text>

            {project.default_model_github_copilot && (
              <>
                <Text className={styles.infoLabel}>Copilot model</Text>
                <Text className={styles.infoValue}>{project.default_model_github_copilot}</Text>
              </>
            )}
          </div>

          <Title3>Runs</Title3>
          {runs.length === 0 ? (
            <Text>No runs yet. Start one above.</Text>
          ) : (
            <div className={styles.runList}>
              {runs.map((r) => <RunRow key={r.run_id} run={r} projectId={projectId} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
}
