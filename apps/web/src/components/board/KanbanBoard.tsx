import { useMemo, useState } from 'react';
import {
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useCtrlScrollZoom, ZoomControls } from './useCtrlScrollZoom';
import { useBoard } from '../../api/board';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { fromDto } from '../../api/agentQueues';
import { AgentRail } from '../AgentRail';
import { KanbanColumn } from './KanbanColumn';
import { columnAccentColor } from './columnMeta';
import { CaptureTaskForm } from './CaptureTaskForm';
import { PickupSettings } from './PickupSettings';
import type { BoardColumnDto, RunCardDto } from '../../api/types';

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
  columnsViewport: {
    overflowX: 'auto',
    paddingBottom: tokens.spacingVerticalS,
    // Slim, styled horizontal scrollbar instead of the heavy default OS bar.
    scrollbarWidth: 'thin',
    scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
    '::-webkit-scrollbar': {
      height: '8px',
    },
    '::-webkit-scrollbar-track': {
      backgroundColor: 'transparent',
    },
    '::-webkit-scrollbar-thumb': {
      backgroundColor: tokens.colorNeutralStroke1,
      borderRadius: tokens.borderRadiusCircular,
    },
    '::-webkit-scrollbar-thumb:hover': {
      backgroundColor: tokens.colorNeutralStroke1Hover,
    },
  },
  columns: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    // Zoom origin: anchor to the top-left so zooming out keeps Backlog in place.
    transformOrigin: 'top left',
  },
  agentRail: {
    padding: `${tokens.spacingVerticalXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
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
  const [selectedAgent, setSelectedAgent] = useState<string | null>(null);

  // Board zoom (board-zoom). Ctrl+Scroll over the columns adjusts the zoom so the
  // user can fit all workflow columns on screen; +/- controls do the same.
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();

  // Derive AgentQueueItems from the board's agent_queues field (Phase 2 — optional).
  const agentItems = useMemo(
    () => (board?.agent_queues ?? []).map(fromDto),
    [board?.agent_queues],
  );

  // When an agent is selected, build the set of run_ids they own for O(1) lookup.
  const selectedRunIds = useMemo<Set<string> | null>(() => {
    if (!selectedAgent) return null;
    const item = agentItems.find((a) => a.agentName === selectedAgent);
    return item?.runIds ? new Set(item.runIds) : null;
  }, [selectedAgent, agentItems]);

  // Filter each column's cards: when a filter is active, show only RunCardDto cards
  // whose run_id appears in the selected agent's run_ids. Task cards (no run_id) are
  // hidden while a filter is active because they belong to the intake queue, not to
  // any specific agent.
  const filteredColumns = useMemo<BoardColumnDto[]>(() => {
    if (!board) return [];
    if (!selectedRunIds) return board.columns;
    return board.columns.map((col) => ({
      ...col,
      cards: col.cards.filter(
        (card): card is RunCardDto =>
          card.kind === 'run' && selectedRunIds.has(card.run_id),
      ),
    }));
  }, [board, selectedRunIds]);

  // Deterministic accent palette keyed by each column's position among the workflow
  // columns (backlog/ready are fixed). Computed once per board layout so colors are
  // stable across renders. The mapping itself lives in columnAccentColor (KanbanColumn).
  const columnsWithAccent = useMemo(() => {
    let workflowIndex = 0;
    return filteredColumns.map((col) => ({
      col,
      accent: columnAccentColor(col.id, col.id === 'backlog' || col.id === 'ready' ? 0 : workflowIndex++),
    }));
  }, [filteredColumns]);

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
        <div className={styles.agentRail}>
          <AgentRail
            agents={agentItems}
            title="Agents"
            selectedAgent={selectedAgent ?? undefined}
            onSelectAgent={(name) => setSelectedAgent(name)}
          />
          {selectedAgent && (
            <span data-testid="agent-rail-filter-active" style={{ display: 'none' }} aria-hidden="true" />
          )}
        </div>
      )}

      {board && (
        <>
          <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
          <div className={styles.columnsViewport} ref={viewportRef}>
            <div className={styles.columns} style={{ zoom }}>
              {columnsWithAccent.map(({ col: column, accent }) => (
                <KanbanColumn
                  key={column.id}
                  column={column}
                  accentColor={accent}
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
          </div>
        </>
      )}

      {board && filteredColumns.length === 0 && !selectedAgent && <Text>No columns to display.</Text>}
    </div>
  );
}
