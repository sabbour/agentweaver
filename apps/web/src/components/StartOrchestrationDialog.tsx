import { useState, useEffect } from 'react';
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
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { FlowRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { WorkflowSummaryDto } from '../api/types';

const useStyles = makeStyles({
  fields: {
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
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [workflowOverride, setWorkflowOverride] = useState<string | null>(null);
  const [manualWorkflows, setManualWorkflows] = useState<WorkflowSummaryDto[]>([]);

  useEffect(() => {
    if (!open) return;
    apiClient.listWorkflows(projectId)
      .then(res => {
        const manual = res.workflows.filter(w => w.trigger?.type === 'manual' && w.id && w.valid);
        setManualWorkflows(manual);
      })
      .catch(() => setManualWorkflows([]));
  }, [open, projectId]);

  const reset = () => {
    setGoal('');
    setError(null);
    setSaving(false);
    setWorkflowOverride(null);
    setManualWorkflows([]);
  };

  const handleSubmit = async () => {
    if (!goal.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const result = await apiClient.startOrchestration(projectId, goal.trim(), workflowOverride || null);
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
      setSaving(false);
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
            <div className={styles.fields}>
              <Text>
                Describe a goal in plain language. The coordinator drafts an outcome spec for your
                review and confirmation before any work is dispatched.
              </Text>
              <Field label="Goal" required>
                <Textarea
                  value={goal}
                  onChange={(_, v) => setGoal(v.value)}
                  placeholder="e.g. Add OAuth sign-in and update the docs and tests."
                  rows={4}
                />
              </Field>
              {manualWorkflows.length > 0 && (
                <Field label="Workflow">
                  <Select
                    value={workflowOverride ?? ''}
                    onChange={(_, d) => setWorkflowOverride(d.value || null)}
                  >
                    <option value="">Auto (coordinator picks)</option>
                    {manualWorkflows.map(w => (
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
              appearance="primary"
              disabled={!goal.trim() || saving}
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
