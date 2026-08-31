import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Button,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Link,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Option,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { FlowRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { parseNoTeamStartError } from '../api/errors';
import type { Project, StartOrchestrationMode, WorkflowSummaryDto } from '../api/types';
import { EmptyState } from './ui';
// Inline action to start an orchestration, with a project selector so the
// user can choose the target project regardless of the current route context.
// Mirrors StartOrchestrationDialog's goal field + submit semantics; adds the
// project picker (ProjectSwitcher's listProjects pattern).

const useStyles = makeStyles({
  startButton: {
    flexShrink: 0,
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  noTeamActions: {
    marginTop: tokens.spacingVerticalS,
  },
});

export interface StartOrchestrationFabProps {
  currentProjectId?: string;
}

export function StartOrchestrationFab({ currentProjectId }: StartOrchestrationFabProps) {
  const styles = useStyles();
  const navigate = useNavigate();

  const [open, setOpen] = useState(false);
  const [projects, setProjects] = useState<Project[]>([]);
  const [loadError, setLoadError] = useState(false);
  const [selectedProjectId, setSelectedProjectId] = useState<string | undefined>(currentProjectId);
  const [goal, setGoal] = useState('');
  const [savingMode, setSavingMode] = useState<StartOrchestrationMode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [noTeamError, setNoTeamError] = useState<string | null>(null);
  const [workflowOverride, setWorkflowOverride] = useState<string | null>(null);
  const [selectableWorkflows, setSelectableWorkflows] = useState<WorkflowSummaryDto[]>([]);

  // Load the project list once the dialog is opened, and default the project
  // selection to the active project at open-time (the FAB lives in AppShell and
  // never remounts, so seeding selection only at mount misses the active project).
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const loadProjects = async () => {
      setSelectedProjectId(currentProjectId);
      try {
        const result = await apiClient.listProjects({ pageSize: 100 });
        if (cancelled) return;
        setProjects(result.items);
        setLoadError(false);
      } catch {
        if (!cancelled) setLoadError(true);
      }
    };
    void loadProjects();
    return () => {
      cancelled = true;
    };
  }, [open, currentProjectId]);

  // Load the selected project's workflows so the user can pick one (matching the
  // Orchestrations-page Start task dialog). Any valid workflow with an id is
  // selectable — including the project's active workflow and event/heartbeat
  // catalog workflows; "Auto" leaves the choice to the coordinator.
  useEffect(() => {
    let cancelled = false;
    const loadWorkflows = async () => {
      setWorkflowOverride(null);
      if (!open || !selectedProjectId) {
        setSelectableWorkflows([]);
        return;
      }
      try {
        const res = await apiClient.listWorkflows(selectedProjectId);
        if (cancelled) return;
        setSelectableWorkflows(res.workflows.filter((w) => w.id && w.valid));
      } catch {
        if (!cancelled) setSelectableWorkflows([]);
      }
    };
    void loadWorkflows();
    return () => {
      cancelled = true;
    };
  }, [open, selectedProjectId]);

  const reset = () => {
    setGoal('');
    setError(null);
    setNoTeamError(null);
    setSavingMode(null);
    setSelectedProjectId(currentProjectId);
    setWorkflowOverride(null);
    setSelectableWorkflows([]);
  };

  const selectedProject = projects.find((p) => p.project_id === selectedProjectId) ?? null;

  const handleSubmit = async (mode: StartOrchestrationMode) => {
    if (!selectedProjectId || !goal.trim()) return;
    setSavingMode(mode);
    setError(null);
    setNoTeamError(null);
    try {
      const result = mode === 'direct'
        ? await apiClient.startOrchestration(selectedProjectId, goal.trim(), workflowOverride, 'direct')
        : await apiClient.startOrchestration(selectedProjectId, goal.trim(), workflowOverride);
      setOpen(false);
      reset();
      navigate(`/projects/${selectedProjectId}/orchestrations/${result.runId}`);
    } catch (err) {
      const noTeam = parseNoTeamStartError(err);
      if (noTeam) {
        setNoTeamError(noTeam.message);
        return;
      }
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
      );
    } finally {
      setSavingMode(null);
    }
  };

  const noProjects = projects.length === 0 && !loadError;
  const saving = savingMode !== null;

  return (
    <Dialog
      open={open}
      onOpenChange={(_, s) => {
        setOpen(s.open);
        if (!s.open) reset();
      }}
    >
      <Tooltip content="Start task" relationship="label" positioning="before">
        <Button
          className={styles.startButton}
          appearance="primary"
          size="small"
          icon={<FlowRegular />}
          aria-label="Start task"
          data-testid="start-task-topbar-action"
          onClick={() => setOpen(true)}
        >
          Start task
        </Button>
      </Tooltip>
      <DialogSurface>
        <DialogBody>
          <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Start a task</DialogTitle>
          <DialogContent>
            <div className={styles.stack}>
              <Text>
                Choose a project and describe a goal in plain language. Direct starts faster from
                your prompt. Define Outcome drafts structured acceptance criteria and expected
                outputs before dispatch. Later review, tool approval, assembly, and merge gates
                still apply.
              </Text>
              {loadError && (
                <MessageBar intent="error">
                  <MessageBarBody>Could not load projects. Try again.</MessageBarBody>
                </MessageBar>
              )}
              {noProjects ? (
                <EmptyState
                  title="Create a project first."
                  description={(
                    <>
                    Open the{' '}
                    <Link
                      onClick={() => {
                        setOpen(false);
                        navigate('/');
                      }}
                    >
                      project gallery
                    </Link>{' '}
                    to add one.
                    </>
                  )}
                />
              ) : (
                <Field label="Project" required>
                  <Combobox
                    aria-label="Project"
                    placeholder="Select project…"
                    disabled={loadError}
                    value={selectedProject?.name ?? ''}
                    selectedOptions={selectedProjectId ? [selectedProjectId] : []}
                    onOptionSelect={(_, data) => {
                      if (data.optionValue) setSelectedProjectId(data.optionValue);
                    }}
                  >
                    {[...projects]
                      .sort((a, b) => a.name.localeCompare(b.name))
                      .map((p) => (
                        <Option key={p.project_id} value={p.project_id} text={p.name}>
                          {p.name}
                        </Option>
                      ))}
                  </Combobox>
                </Field>
              )}
              <Field label="Goal" required>
                <Textarea
                  value={goal}
                  onChange={(_, v) => setGoal(v.value)}
                  placeholder="e.g. Add OAuth sign-in and update the docs and tests."
                  rows={4}
                  disabled={noProjects}
                />
              </Field>
              {selectableWorkflows.length > 0 && (
                <Field label="Workflow">
                  <Select
                    value={workflowOverride ?? ''}
                    onChange={(_, d) => setWorkflowOverride(d.value || null)}
                    disabled={noProjects}
                  >
                    <option value="">Auto (coordinator picks)</option>
                    {selectableWorkflows.map((w) => (
                      <option key={w.id} value={w.id!}>{w.name ?? w.id}</option>
                    ))}
                  </Select>
                </Field>
              )}
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
              {noTeamError && selectedProjectId && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    {noTeamError}
                    <div className={styles.noTeamActions}>
                      <Button
                        appearance="primary"
                        onClick={() => {
                          setOpen(false);
                          navigate(`/projects/${selectedProjectId}/team/cast`);
                        }}
                      >
                        Cast a team
                      </Button>
                    </div>
                  </MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={saving} onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button
              appearance="secondary"
              disabled={!selectedProjectId || !goal.trim() || saving}
              onClick={() => void handleSubmit('define_outcome')}
            >
              {savingMode === 'define_outcome' ? 'Defining' : 'Define Outcome'}
            </Button>
            <Button
              appearance="primary"
              disabled={!selectedProjectId || !goal.trim() || saving}
              onClick={() => void handleSubmit('direct')}
            >
              {savingMode === 'direct' ? 'Starting' : 'Direct'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
