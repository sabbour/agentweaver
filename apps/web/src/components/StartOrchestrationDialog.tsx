import {
  apiClient } from '../api/apiClient';
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
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { FlowRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import { formatApiErrorMessage, parseNoTeamStartError } from '../api/errors';
import type { StartOrchestrationMode, WorkflowSummaryDto } from '../api/types';
import {
  AiExecutionProviderHint,
  AiExecutionProviderReadiness,
  AiExecutionProviderStatus,
  AiProviderChangeAnnouncement,
} from './AiExecutionProviderHint';
import { useAiExecutionContext } from '../hooks/useAiExecutionContext';

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  noTeamActions: {
    marginTop: tokens.spacingVerticalS,
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
  const [noTeamError, setNoTeamError] = useState<string | null>(null);
  const [workflowOverride, setWorkflowOverride] = useState<string | null>(null);
  const [autoApproveTools, setAutoApproveTools] = useState(false);
  const [autopilot, setAutopilot] = useState(false);
  const [selectableWorkflows, setSelectableWorkflows] = useState<WorkflowSummaryDto[]>([]);
  const providerContext = useAiExecutionContext('orchestration', projectId);
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
    setNoTeamError(null);
    setSavingMode(null);
    setWorkflowOverride(null);
    setAutoApproveTools(false);
    setAutopilot(false);
    setSelectableWorkflows([]);
  };

  const handleSubmit = async (mode: StartOrchestrationMode) => {
    if (!goal.trim()) return;
    setSavingMode(mode);
    setError(null);
    setNoTeamError(null);
    try {
      const result = mode === 'direct'
        ? await apiClient.startOrchestration(
            projectId,
            goal.trim(),
            workflowOverride || null,
            'direct',
            providerContext.providerKey,
            { auto_approve_tools: autoApproveTools, autopilot })
        : await apiClient.startOrchestration(
            projectId,
            goal.trim(),
            workflowOverride || null,
            undefined,
            providerContext.providerKey,
            { auto_approve_tools: autoApproveTools, autopilot });
      setOpen(false);
      reset();
      onStarted(result.runId);
    } catch (err) {
      if (providerContext.handleInvocationError(err)) {
        setError('The AI provider changed. Review the updated provider and start again.');
        return;
      }
      const noTeam = parseNoTeamStartError(err);
      if (noTeam) {
        setNoTeamError(noTeam.message);
        return;
      }
      setError(
        formatApiErrorMessage(err),
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
                Describe a goal in plain language. Direct starts faster from your prompt. Define
                Outcome drafts structured acceptance criteria and expected outputs before dispatch.
                Later review, tool approval, assembly, and merge gates still apply.
              </Text>
              <AiExecutionProviderReadiness
                context={providerContext.context}
                error={providerContext.error}
                projectId={projectId}
                onRefresh={() => void providerContext.refresh()}
              />
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
              <div role="group" aria-label="Run approval policy">
                <Text weight="semibold">Run approval policy</Text>
                <Switch
                  label="Auto-approve safe tools"
                  checked={autoApproveTools}
                  onChange={(_, data) => setAutoApproveTools(data.checked)}
                />
                <Switch
                  label="Autopilot"
                  checked={autopilot}
                  onChange={(_, data) => setAutopilot(data.checked)}
                />
                <Text size={200}>
                  Safe-tool approval covers only repository-defined safe tools. Preview,
                  destructive, privileged, secret, and other network approvals remain gated.
                </Text>
              </div>
              {error && (
                <MessageBar intent="error">
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
              {noTeamError && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    {noTeamError}
                    <div className={styles.noTeamActions}>
                      <Button appearance="primary" as="a" href={`/projects/${encodeURIComponent(projectId)}/team/cast`}>
                        Cast a team
                      </Button>
                    </div>
                  </MessageBarBody>
                </MessageBar>
              )}
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={saving}>Cancel</Button>
            </DialogTrigger>
            <AiExecutionProviderStatus
              context={providerContext.context}
              loading={providerContext.loading}
              error={providerContext.error}
            />
            <AiExecutionProviderHint
              context={providerContext.context}
              loading={providerContext.loading}
              required={!goal.trim()}
              showIndicator={false}
            >
              <Button
                appearance="secondary"
                disabled={!goal.trim() || saving || providerContext.loading || !providerContext.available}
                onClick={() => void handleSubmit('define_outcome')}
              >
                {savingMode === 'define_outcome' ? 'Defining' : 'Define Outcome'}
              </Button>
            </AiExecutionProviderHint>
            <AiExecutionProviderHint
              context={providerContext.context}
              loading={providerContext.loading}
              required={!goal.trim()}
              showIndicator={false}
            >
              <Button
                appearance="primary"
                disabled={!goal.trim() || saving || providerContext.loading || !providerContext.available}
                onClick={() => void handleSubmit('direct')}
              >
                {savingMode === 'direct' ? 'Starting' : 'Direct'}
              </Button>
            </AiExecutionProviderHint>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
            <AiProviderChangeAnnouncement message={providerContext.announcement} />
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
