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
  Select,
  Spinner,
  Text,
  Textarea,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { CreateRunRequest, Project, WorkflowRunDto, TeamMemberDto } from '../api/types';

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
  const [agentName, setAgentName] = useState('');
  const [branch, setBranch] = useState('main');
  const [members, setMembers] = useState<TeamMemberDto[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    apiClient.getTeam(projectId)
      .then((t) => {
        if (!cancelled) {
          const active = (t?.members ?? []).filter((m) => m.status === 'active' && !m.is_built_in);
          setMembers(active);
          if (active.length > 0 && !agentName) setAgentName(active[0].name);
        }
      })
      .catch(() => {});
    return () => { cancelled = true; };
  // agentName intentionally excluded — only reset on open
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, projectId]);

  const reset = () => {
    setTask('');
    setAgentName('');
    setBranch('main');
    setMembers([]);
    setError(null);
    setSaving(false);
  };

  const handleSubmit = async () => {
    if (!task.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const req: CreateRunRequest = {
        originating_branch: branch.trim() || 'main',
        task: task.trim(),
        agent_name: agentName || undefined,
      };
      const result = await apiClient.createProjectRun(projectId, req);
      setOpen(false);
      reset();
      onStarted(result.workflow_run_id);
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
              <Field label="Agent" required>
                <Select
                  value={agentName}
                  onChange={(_, v) => setAgentName(v.value)}
                  disabled={members.length === 0}
                >
                  {members.length === 0 && <option value="">No active agents</option>}
                  {members.map((m) => (
                    <option key={m.name} value={m.name}>{m.name} — {m.role_title}</option>
                  ))}
                </Select>
              </Field>
              <Field label="Task" required>
                <Textarea
                  value={task}
                  onChange={(_, v) => setTask(v.value)}
                  placeholder="Describe what the agent should do..."
                  rows={4}
                />
              </Field>
              <Field label="Branch">
                <Input value={branch} onChange={(_, v) => setBranch(v.value)} placeholder="main" />
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
              disabled={!task.trim() || members.length === 0 || saving}
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

function RunRow({ run, projectId, onDeleted }: { run: WorkflowRunDto; projectId: string; onDeleted: (workflowRunId: string) => void }) {
  const styles = useStyles();
  const [acting, setActing] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isTerminal = ['completed', 'merged', 'failed', 'merge_failed', 'declined'].includes(run.status);
  const isAbandonable = !isTerminal;

  const handleConfirmed = async () => {
    setConfirmOpen(false);
    setActing(true);
    setError(null);
    try {
      await apiClient.deleteRun(run.execution_id);
      onDeleted(run.workflow_run_id);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Action failed.');
      setActing(false);
    }
  };

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
      <Text className={styles.runMeta}>{new Date(run.started_at).toLocaleString()}</Text>
      <Link to={`/projects/${projectId}/runs/${run.workflow_run_id ?? run.execution_id}/workflow`} style={{ textDecoration: 'none' }}>
        <Button appearance="secondary">Workflow</Button>
      </Link>
      {isAbandonable && (
        <>
          <Button appearance="subtle" icon={<DismissCircleRegular />} disabled={acting} onClick={() => setConfirmOpen(true)} aria-label="Abandon run">
            Abandon
          </Button>
          <Dialog open={confirmOpen} onOpenChange={(_, d) => setConfirmOpen(d.open)}>
            <DialogSurface>
              <DialogBody>
                <DialogTitle>Abandon run?</DialogTitle>
                <DialogContent>
                  This will abandon the run and discard any pending changes. This cannot be undone.
                  {error && <Text style={{ color: 'red', display: 'block', marginTop: 8 }}>{error}</Text>}
                </DialogContent>
                <DialogActions>
                  <DialogTrigger disableButtonEnhancement>
                    <Button appearance="secondary">Cancel</Button>
                  </DialogTrigger>
                  <Button appearance="primary" onClick={() => void handleConfirmed()}>
                    Abandon
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </>
      )}
      {isTerminal && (
        <>
          <Button
            appearance="subtle"
            icon={<DeleteRegular />}
            disabled={acting}
            onClick={() => setConfirmOpen(true)}
            aria-label="Delete run"
          />
          <Dialog open={confirmOpen} onOpenChange={(_, d) => setConfirmOpen(d.open)}>
            <DialogSurface>
              <DialogBody>
                <DialogTitle>Delete run?</DialogTitle>
                <DialogContent>
                  This will permanently delete the run and cannot be undone.
                  {error && <Text style={{ color: 'red', display: 'block', marginTop: 8 }}>{error}</Text>}
                </DialogContent>
                <DialogActions>
                  <DialogTrigger disableButtonEnhancement>
                    <Button appearance="secondary">Cancel</Button>
                  </DialogTrigger>
                  <Button appearance="primary" onClick={() => void handleConfirmed()}>
                    Delete
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </>
      )}
    </div>
  );
}

export function ProjectPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const handleRunDeleted = (workflowRunId: string) => {
    setRuns((prev) => prev.filter((r) => r.workflow_run_id !== workflowRunId));
  };

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;

    const TERMINAL = ['completed', 'merged', 'failed', 'merge_failed', 'declined'];

    const fetchRuns = async () => {
      try {
        const runList = await apiClient.listProjectRuns(projectId);
        if (!cancelled) setRuns([...runList].reverse());
        return runList;
      } catch {
        return null;
      }
    };

    Promise.all([
      apiClient.getProject(projectId),
      fetchRuns(),
    ])
      .then(([proj, runList]) => {
        if (!cancelled) {
          setProject(proj);
        }
        // Kick off polling while any run is non-terminal
        if (!runList) return;
        const hasLive = runList.some(r => !TERMINAL.includes(r.status));
        if (!hasLive) return;
        const iv = setInterval(() => {
          if (cancelled) { clearInterval(iv); return; }
          void fetchRuns().then(latest => {
            if (latest && latest.every(r => TERMINAL.includes(r.status))) {
              clearInterval(iv);
            }
          });
        }, 5000);
        // Store interval id via closure — cleaned up when cancelled
        return () => clearInterval(iv);
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
                onStarted={(runId) => navigate(`/projects/${projectId}/runs/${runId}/workflow`)}
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

            <Text className={styles.infoLabel}>Repository path</Text>
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
              {runs.map((r) => <RunRow key={r.workflow_run_id} run={r} projectId={projectId} onDeleted={handleRunDeleted} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
}
