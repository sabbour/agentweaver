import { useState } from 'react';
import {
  Button,
  Caption1,
  Field,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArchiveRegular, CheckmarkRegular, DismissRegular, EditRegular, FlowRegular } from '@fluentui/react-icons';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { TaskCardDto, WorkflowSummaryDto } from '../../api/types';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    cursor: 'grab',
  },
  dragging: {
    opacity: 0.5,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalXS,
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    wordBreak: 'break-word',
  },
  description: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  meta: {
    color: tokens.colorNeutralForeground3,
  },
  cardActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXS,
  },
  editFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  editActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    justifyContent: 'flex-end',
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

export interface TaskCardProps {
  card: TaskCardDto;
  columnId: string;
  projectId: string;
  onMutated: () => void | Promise<void>;
  onDragStartTask: (taskId: string, sourceColumnId: string) => void;
  onDragEndTask: () => void;
  isDragging: boolean;
}

export function TaskCard({ card, columnId, projectId, onMutated, onDragStartTask, onDragEndTask, isDragging }: TaskCardProps) {
  const styles = useStyles();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(card.title);
  const [description, setDescription] = useState(card.description ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [workflows, setWorkflows] = useState<WorkflowSummaryDto[] | null>(null);
  const [workflowsLoading, setWorkflowsLoading] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const reportError = (e: unknown) => {
    setError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
  };

  const loadWorkflows = async () => {
    if (workflows || workflowsLoading) return;
    setWorkflowsLoading(true);
    try {
      const list = await apiClient.listWorkflows(projectId);
      setWorkflows(list.workflows);
    } catch (e) {
      reportError(e);
    } finally {
      setWorkflowsLoading(false);
    }
  };

  const handleSetOverride = async (workflowId: string | null) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await apiClient.setTaskWorkflowOverride(projectId, card.task_id, workflowId);
      setNotice(workflowId ? 'Workflow override set.' : 'Workflow override cleared.');
      await onMutated();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setError('This task was just claimed — its workflow can no longer be changed.');
        await onMutated();
      } else {
        reportError(e);
      }
    } finally {
      setBusy(false);
    }
  };

  const handleSave = async () => {
    if (!title.trim()) {
      setError('Title is required.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await apiClient.editBacklogTask(projectId, card.task_id, {
        title: title.trim(),
        description: description.trim() ? description.trim() : null,
      });
      setEditing(false);
      await onMutated();
    } catch (e) {
      reportError(e);
    } finally {
      setBusy(false);
    }
  };

  const handleArchive = async () => {
    setBusy(true);
    setError(null);
    try {
      await apiClient.archiveBacklogTask(projectId, card.task_id);
      await onMutated();
    } catch (e) {
      reportError(e);
      setBusy(false);
    }
  };

  if (editing) {
    return (
      <div className={styles.card}>
        <div className={styles.editFields}>
          <Field label="Title" required>
            <Input value={title} onChange={(_, v) => setTitle(v.value)} disabled={busy} />
          </Field>
          <Field label="Description">
            <Textarea value={description} onChange={(_, v) => setDescription(v.value)} disabled={busy} rows={3} />
          </Field>
          {error && <Text className={styles.error}>{error}</Text>}
          <div className={styles.editActions}>
            <Button
              appearance="secondary"
              size="small"
              icon={<DismissRegular />}
              disabled={busy}
              onClick={() => { setEditing(false); setTitle(card.title); setDescription(card.description ?? ''); setError(null); }}
            >
              Cancel
            </Button>
            <Button appearance="primary" size="small" icon={<CheckmarkRegular />} disabled={busy || !title.trim()} onClick={() => void handleSave()}>
              Save
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div
      className={`${styles.card}${isDragging ? ` ${styles.dragging}` : ''}`}
      draggable
      onDragStart={(e) => {
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('application/agentweaver-task', JSON.stringify({ taskId: card.task_id, sourceColumnId: columnId }));
        e.dataTransfer.setData('text/plain', card.task_id);
        onDragStartTask(card.task_id, columnId);
      }}
      onDragEnd={() => onDragEndTask()}
      data-testid={`task-card-${card.task_id}`}
    >
      <div className={styles.header}>
        <Text className={styles.title}>{card.title}</Text>
        <div className={styles.cardActions}>
          <Menu onOpenChange={(_, d) => { if (d.open) void loadWorkflows(); }}>
            <MenuTrigger disableButtonEnhancement>
              <Button appearance="subtle" size="small" icon={<FlowRegular />} aria-label="Set workflow" disabled={busy} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {workflowsLoading && <MenuItem disabled>Loading workflows…</MenuItem>}
                {!workflowsLoading && workflows?.filter((wf) => wf.valid && wf.id).map((wf) => (
                  <MenuItem key={wf.id} onClick={() => void handleSetOverride(wf.id)}>
                    {wf.name ?? wf.id}{wf.is_default ? ' (default)' : ''}
                  </MenuItem>
                ))}
                {!workflowsLoading && workflows && workflows.filter((wf) => wf.valid && wf.id).length === 0 && (
                  <MenuItem disabled>No valid workflows</MenuItem>
                )}
                <MenuItem onClick={() => void handleSetOverride(null)}>Use project default</MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
          <Button appearance="subtle" size="small" icon={<EditRegular />} aria-label="Edit task" disabled={busy} onClick={() => setEditing(true)} />
          <Button appearance="subtle" size="small" icon={<ArchiveRegular />} aria-label="Archive task" disabled={busy} onClick={() => void handleArchive()} />
        </div>
      </div>
      {card.description && <Text className={styles.description}>{card.description}</Text>}
      <Caption1 className={styles.meta}>{card.captured_by}</Caption1>
      {notice && <Caption1 className={styles.meta}>{notice}</Caption1>}
      {error && <Text className={styles.error}>{error}</Text>}
    </div>
  );
}
