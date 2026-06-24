import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Link,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  Textarea,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { FlowRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project } from '../api/types';

// Global floating action button to start an orchestration from any page, with a
// project selector so the user can choose the target project regardless of the
// current route context. Mirrors StartOrchestrationDialog's goal field + submit
// semantics; adds the project picker (ProjectSwitcher's listProjects pattern).

const useStyles = makeStyles({
  fab: {
    position: 'fixed',
    bottom: tokens.spacingVerticalXXL,
    right: tokens.spacingHorizontalXXL,
    zIndex: 100,
    borderRadius: tokens.borderRadiusCircular,
    boxShadow: tokens.shadow16,
  },
  fields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
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
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Load the project list once the dialog is opened, and default the project
  // selection to the active project at open-time (the FAB lives in AppShell and
  // never remounts, so seeding selection only at mount misses the active project).
  useEffect(() => {
    if (!open) return;
    setSelectedProjectId(currentProjectId);
    let cancelled = false;
    apiClient
      .listProjects()
      .then((list) => {
        if (cancelled) return;
        setProjects(list);
        setLoadError(false);
      })
      .catch(() => {
        if (!cancelled) setLoadError(true);
      });
    return () => {
      cancelled = true;
    };
  }, [open, currentProjectId]);

  const reset = () => {
    setGoal('');
    setError(null);
    setSaving(false);
    setSelectedProjectId(currentProjectId);
  };

  const selectedProject = projects.find((p) => p.project_id === selectedProjectId) ?? null;

  const handleSubmit = async () => {
    if (!selectedProjectId || !goal.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const result = await apiClient.startOrchestration(selectedProjectId, goal.trim());
      setOpen(false);
      reset();
      navigate(`/projects/${selectedProjectId}/orchestrations/${result.runId}`);
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

  const noProjects = projects.length === 0 && !loadError;

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
          className={styles.fab}
          appearance="primary"
          size="large"
          icon={<FlowRegular />}
          aria-label="Start task"
          onClick={() => setOpen(true)}
        >
          Start task
        </Button>
      </Tooltip>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Start a task</DialogTitle>
          <DialogContent>
            <div className={styles.fields}>
              <Text>
                Choose a project and describe a goal in plain language. The coordinator drafts an
                outcome spec for your review and confirmation before any work is dispatched.
              </Text>
              {loadError && (
                <MessageBar intent="error">
                  <MessageBarBody>Could not load projects. Try again.</MessageBarBody>
                </MessageBar>
              )}
              {noProjects ? (
                <MessageBar intent="info">
                  <MessageBarBody>
                    Create a project first. Open the{' '}
                    <Link
                      onClick={() => {
                        setOpen(false);
                        navigate('/');
                      }}
                    >
                      project gallery
                    </Link>{' '}
                    to add one.
                  </MessageBarBody>
                </MessageBar>
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
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={saving} onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!selectedProjectId || !goal.trim() || saving}
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
