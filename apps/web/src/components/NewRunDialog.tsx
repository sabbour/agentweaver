import { useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { TeamMemberDto, CreateProjectRunResponse } from '../api/types';

interface NewRunDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  projectId: string;
  members: TeamMemberDto[];
  onRunCreated: (run: CreateProjectRunResponse) => void;
}

export function NewRunDialog({ open, onOpenChange, projectId, members, onRunCreated }: NewRunDialogProps) {
  const activeMembers = members.filter((m) => m.status === 'active' && !m.is_built_in);

  const [agentName, setAgentName] = useState(activeMembers[0]?.name ?? '');
  const [task, setTask] = useState('');
  const [branch, setBranch] = useState('main');
  const [submitting, setSubmitting] = useState(false);
  const [taskError, setTaskError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleClose = () => {
    setAgentName(activeMembers[0]?.name ?? '');
    setTask('');
    setBranch('main');
    setTaskError(null);
    setError(null);
    setSubmitting(false);
    onOpenChange(false);
  };

  const handleSubmit = async () => {
    if (!task.trim()) {
      setTaskError('Task is required.');
      return;
    }
    setTaskError(null);
    setSubmitting(true);
    setError(null);
    try {
      const run = await apiClient.createProjectRun(projectId, {
        originating_branch: branch.trim() || 'main',
        task: task.trim(),
        agent_name: agentName || undefined,
      });
      onRunCreated(run);
      handleClose();
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, s) => { if (!s.open) handleClose(); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>New Run</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <Field label="Agent" required>
                <Select
                  value={agentName}
                  onChange={(_, v) => { setAgentName(v.value); }}
                  disabled={activeMembers.length === 0}
                >
                  {activeMembers.length === 0 && (
                    <option value="">No active members</option>
                  )}
                  {activeMembers.map((m) => (
                    <option key={m.name} value={m.name}>
                      {m.name} — {m.role_title}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field
                label="Task"
                required
                validationMessage={taskError ?? undefined}
                validationState={taskError ? 'error' : 'none'}
              >
                <Textarea
                  value={task}
                  onChange={(_, v) => { setTask(v.value); if (v.value.trim()) setTaskError(null); }}
                  placeholder="Describe what you want the agent to do..."
                  rows={4}
                />
              </Field>
              <Field label="Branch">
                <Input
                  value={branch}
                  onChange={(_, v) => { setBranch(v.value); }}
                  placeholder="main"
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
            <Button appearance="secondary" onClick={handleClose} disabled={submitting}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              onClick={() => { void handleSubmit(); }}
              disabled={submitting || activeMembers.length === 0}
            >
              {submitting ? 'Starting...' : 'Start run'}
            </Button>
            {submitting && <Spinner size="extra-tiny" aria-hidden="true" />}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
