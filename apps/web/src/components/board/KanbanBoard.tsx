import { useState } from 'react';
import {
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useBoard } from '../../api/board';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { KanbanColumn } from './KanbanColumn';
import { CaptureTaskForm } from './CaptureTaskForm';
import { PickupSettings } from './PickupSettings';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  toolbar: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  capture: {
    flex: 1,
    minWidth: '280px',
  },
  columns: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    overflowX: 'auto',
    alignItems: 'flex-start',
    paddingBottom: tokens.spacingVerticalS,
  },
});

export interface KanbanBoardProps {
  projectId: string;
  // Test seam: shorten the poll interval to avoid timing flakiness.
  pollIntervalMs?: number;
}

// Per-project homepage Kanban board (FR-013). Columns are built dynamically from the
// board API response (FR-015) — Backlog and Ready first (server-ordered), then the
// coordinator workflow-stage columns. Drag is constrained to Backlog<->Ready by the
// column drop handlers and the server (workflow columns never accept a task move).
export function KanbanBoard({ projectId, pollIntervalMs }: KanbanBoardProps) {
  const styles = useStyles();
  const [includeTerminalHistory, setIncludeTerminalHistory] = useState(false);
  const { board, status, error, refetch } = useBoard(projectId, {
    intervalMs: pollIntervalMs,
    includeTerminalHistory,
  });

  const [draggingTaskId, setDraggingTaskId] = useState<string | null>(null);
  const [rejectMessage, setRejectMessage] = useState<string | null>(null);
  const [mutationError, setMutationError] = useState<string | null>(null);

  const handleDropTask = async (taskId: string, sourceColumnId: string, targetColumnId: string, targetIndex: number) => {
    setDraggingTaskId(null);
    setRejectMessage(null);
    setMutationError(null);
    try {
      if (sourceColumnId === targetColumnId) {
        // Within-bucket reorder (FR-018a).
        await apiClient.reorderBacklogTask(projectId, taskId, targetIndex);
      } else if (targetColumnId === 'ready') {
        await apiClient.moveTaskToReady(projectId, taskId, targetIndex);
      } else if (targetColumnId === 'backlog') {
        await apiClient.moveTaskToBacklog(projectId, taskId, targetIndex);
      } else {
        // Defensive: only intake columns invoke this handler.
        setRejectMessage('Only the coordinator moves work into the workflow.');
        return;
      }
      await refetch();
    } catch (e) {
      setMutationError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
    }
  };

  const handleRejectDrop = () => {
    setDraggingTaskId(null);
    setRejectMessage('Only the coordinator moves work into the workflow.');
  };

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <div className={styles.capture}>
          <CaptureTaskForm projectId={projectId} onCaptured={refetch} />
        </div>
        <PickupSettings projectId={projectId} />
      </div>

      {status === 'loading' && !board && <Spinner label="Loading board" />}

      {status === 'error' && error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {board && !board.workflow_stages_available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Workflow columns are unavailable. Showing Backlog and Ready only.
          </MessageBarBody>
        </MessageBar>
      )}

      {rejectMessage && (
        <MessageBar intent="warning" data-testid="reject-message">
          <MessageBarBody>{rejectMessage}</MessageBarBody>
        </MessageBar>
      )}

      {mutationError && (
        <MessageBar intent="error">
          <MessageBarBody>{mutationError}</MessageBarBody>
        </MessageBar>
      )}

      {board && (
        <div className={styles.columns}>
          {board.columns.map((column) => (
            <KanbanColumn
              key={column.id}
              column={column}
              projectId={projectId}
              onMutated={refetch}
              onDropTask={(taskId, sourceColumnId, targetColumnId, targetIndex) =>
                void handleDropTask(taskId, sourceColumnId, targetColumnId, targetIndex)}
              onRejectDrop={handleRejectDrop}
              onDragStartTask={(taskId) => setDraggingTaskId(taskId)}
              onDragEndTask={() => setDraggingTaskId(null)}
              draggingTaskId={draggingTaskId}
              includeTerminalHistory={includeTerminalHistory}
              onToggleTerminalHistory={() => setIncludeTerminalHistory((v) => !v)}
            />
          ))}
        </div>
      )}

      {board && board.columns.length === 0 && <Text>No columns to display.</Text>}
    </div>
  );
}
