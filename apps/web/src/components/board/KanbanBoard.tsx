import { useMemo, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowImportRegular } from '@fluentui/react-icons';
import { useCtrlScrollZoom, ZoomControls } from './useCtrlScrollZoom';
import { useBoard } from '../../api/board';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { KanbanColumn } from './KanbanColumn';
import { columnAccentColor, fixedBoardColumns } from './columnMeta';
import { CaptureTaskForm } from './CaptureTaskForm';
import { PickupSettings } from './PickupSettings';
import { WorkspaceFilePicker } from '../WorkspaceFilePicker';
import { DecomposePreviewDialog } from '../DecomposePreviewDialog';
import type { ProposedBacklogItem } from '../../api/types';

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
  toolbarActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
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
  problemsSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  problemsSectionLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
    paddingLeft: tokens.spacingHorizontalXS,
  },
  problemsColumns: {
    display: 'flex',
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    overflowX: 'auto',
    paddingBottom: tokens.spacingVerticalXS,
    scrollbarWidth: 'thin',
    scrollbarColor: `${tokens.colorNeutralStroke1} transparent`,
  },
});

export interface KanbanBoardProps {
  projectId: string;
  // Test seam: shorten the poll interval to avoid timing flakiness.
  pollIntervalMs?: number;
}

// Per-project homepage Kanban board. The board API remains stage-aware, but the UI
// presents a stable six-bucket view: Backlog, Ready, Active, Human Review, Done (main
// row) + Problems (separate highlighted section below). Drag is constrained to
// Backlog<->Ready by the column drop handlers and the server (workflow columns never
// accept a task move).
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

  // "Import from workspace" state
  const [importPickerOpen, setImportPickerOpen] = useState(false);
  const [importSelectedPath, setImportSelectedPath] = useState<string | null>(null);
  const [decomposePreviewOpen, setDecomposePreviewOpen] = useState(false);
  const [decomposeItems, setDecomposeItems] = useState<ProposedBacklogItem[]>([]);
  const [decomposeWasCapped, setDecomposeWasCapped] = useState(false);
  const [decomposeTotal, setDecomposeTotal] = useState(0);
  const [decomposeLoading, setDecomposeLoading] = useState(false);
  const [decomposeError, setDecomposeError] = useState<string | null>(null);
  const [importSuccess, setImportSuccess] = useState(false);

  // Board zoom (board-zoom). Ctrl+Scroll over the columns adjusts the zoom so the
  // user can fit all workflow columns on screen; +/- controls do the same.
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();

  const visibleColumns = useMemo(() => (board ? fixedBoardColumns(board.columns) : []), [board]);

  // Deterministic accent palette keyed by each visible fixed column.
  const columnsWithAccent = useMemo(() => {
    let workflowIndex = 0;
    return visibleColumns.map((col) => ({
      col,
      accent: columnAccentColor(col.id, col.id === 'backlog' || col.id === 'ready' ? 0 : workflowIndex++),
    }));
  }, [visibleColumns]);

  const mainColumns = useMemo(() => columnsWithAccent.filter(({ col }) => col.id !== 'problems'), [columnsWithAccent]);
  const problemsEntry = useMemo(() => columnsWithAccent.find(({ col }) => col.id === 'problems'), [columnsWithAccent]);

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

  const handleImportPickerConfirm = async () => {
    if (!importSelectedPath) return;
    setImportPickerOpen(false);
    setDecomposeLoading(true);
    setDecomposeError(null);
    setDecomposeItems([]);
    setImportSuccess(false);
    setDecomposePreviewOpen(true);
    try {
      const result = await apiClient.decomposeSpec(projectId, importSelectedPath, false, null, undefined);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  const handleDecomposeConfirm = async () => {
    if (!importSelectedPath) return;
    setDecomposeLoading(true);
    setDecomposeError(null);
    try {
      const result = await apiClient.decomposeSpec(projectId, importSelectedPath, true, null, undefined);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
      setDecomposePreviewOpen(false);
      setImportSuccess(true);
      setImportSelectedPath(null);
      await refetch();
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <div className={styles.capture}>
          <CaptureTaskForm projectId={projectId} onCaptured={refetch} />
        </div>
        <div className={styles.toolbarActions}>
          <Button
            appearance="secondary"
            icon={<ArrowImportRegular />}
            onClick={() => { setImportSelectedPath(null); setImportPickerOpen(true); setImportSuccess(false); }}
          >
            Import from workspace
          </Button>
          <PickupSettings projectId={projectId} />
        </div>
      </div>

      {importSuccess && (
        <MessageBar intent="success">
          <MessageBarBody>Tasks imported successfully.</MessageBarBody>
        </MessageBar>
      )}

      {status === 'loading' && !board && <Spinner label="Loading board" />}

      {status === 'error' && error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {board && !board.workflow_stages_available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Workflow columns are unavailable. Workflow buckets may be empty until the API recovers.
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
        <>
          <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
          <div className={styles.columnsViewport} ref={viewportRef}>
            <div className={styles.columns} style={{ zoom }}>
              {mainColumns.map(({ col: column, accent }) => (
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

          {problemsEntry && (
            <div className={styles.problemsSection}>
              <span className={styles.problemsSectionLabel}>Needs attention</span>
              <div className={styles.problemsColumns}>
                <KanbanColumn
                  key={problemsEntry.col.id}
                  column={problemsEntry.col}
                  accentColor={problemsEntry.accent}
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
              </div>
            </div>
          )}
        </>
      )}

      {board && columnsWithAccent.length === 0 && <Text>No columns to display.</Text>}

      {/* Workspace file picker dialog */}
      <Dialog open={importPickerOpen} onOpenChange={(_, d) => { if (!d.open) setImportPickerOpen(false); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Import from workspace</DialogTitle>
            <DialogContent>
              <WorkspaceFilePicker
                projectId={projectId}
                selectedPath={importSelectedPath}
                onSelect={setImportSelectedPath}
              />
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setImportPickerOpen(false)}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={!importSelectedPath}
                onClick={() => void handleImportPickerConfirm()}
              >
                Preview tasks
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <DecomposePreviewDialog
        isOpen={decomposePreviewOpen}
        onClose={() => { setDecomposePreviewOpen(false); setDecomposeError(null); }}
        onConfirm={handleDecomposeConfirm}
        proposedItems={decomposeItems}
        wasCapped={decomposeWasCapped}
        totalFound={decomposeTotal}
        isLoading={decomposeLoading}
        error={decomposeError}
      />
    </div>
  );
}
