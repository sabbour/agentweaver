import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
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
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { FlowRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import type { StartOrchestrationMode, WorkflowSummaryDto } from '../api/types';

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});
interface StartOrchestrationDialogProps {
  projectId: string;
  onStarted: (runId: string) => void;
}

export function StartOrchestrationDialog({ projectId, onStarted }: StartOrchestrationDialogProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [goal, setGoal] = useState('');
  const [savingMode, setSavingMode] = useState<StartOrchestrationMode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [workflowOverride, setWorkflowOverride] = useState<string | null>(null);
  const [selectableWorkflows, setSelectableWorkflows] = useState<WorkflowSummaryDto[]>([]);
  const saving = savingMode !== null;

  useEffect(() => {
    if (!open) return;
    apiClient.listWorkflows(projectId)
      .then(res => {
        // Any valid workflow with an id can be run manually.
        const selectable = res.workflows.filter(w => w.id && w.valid);
        setSelectableWorkflows(selectable);
      })
      .catch(() => setSelectableWorkflows([]));
  }, [open, projectId]);

  const reset = () => {
    setGoal('');
    setError(null);
    setSavingMode(null);
    setWorkflowOverride(null);
    setSelectableWorkflows([]);
  };

  const handleSubmit = async (mode: StartOrchestrationMode) => {
    if (!goal.trim()) return;
    setSavingMode(mode);
    setError(null);
    try {
      const result = mode === 'direct'
        ? await apiClient.startOrchestration(projectId, goal.trim(), workflowOverride || null, 'direct')
        : await apiClient.startOrchestration(projectId, goal.trim(), workflowOverride || null);
      setOpen(false);
      reset();
      onStarted(result.runId);
    } catch (err) {
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

  return (
    <Dialog open={open} onOpenChange={(_, s) => { setOpen(s.open); if (!s.open) reset(); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="primary" icon={<FlowRegular />}>Start task</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Start a task</DialogTitle>
          <DialogContent>
            <div className={styles.stack}>
              <Text>
                Describe a goal in plain language. Direct starts faster from your prompt. Define
                Outcome drafts structured acceptance criteria and expected outputs before dispatch.
                Later review, tool approval, assembly, and merge gates still apply.
              </Text>
              <Field label="Goal" required>
                <Textarea
                  value={goal}
                  onChange={(_, v) => setGoal(v.value)}
                  placeholder="e.g. Add OAuth sign-in and update the docs and tests."
                  rows={4}
                />
              </Field>
              {selectableWorkflows.length > 0 && (
                <Field label="Workflow">
                  <Select
                    value={workflowOverride ?? ''}
                    onChange={(_, d) => setWorkflowOverride(d.value || null)}
                  >
                    <option value="">Auto (coordinator picks)</option>
                    {selectableWorkflows.map(w => (
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
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={saving}>Cancel</Button>
            </DialogTrigger>
            <Button
              appearance="secondary"
              disabled={!goal.trim() || saving}
              onClick={() => void handleSubmit('define_outcome')}
            >
              {savingMode === 'define_outcome' ? 'Defining' : 'Define Outcome'}
            </Button>
            <Button
              appearance="primary"
              disabled={!goal.trim() || saving}
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
