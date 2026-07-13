import {
  apiClient } from '../../api/apiClient';
import { useBoard } from '../../api/board';
import { ApiError } from '../../api/client';
import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, makeStyles, mergeClasses, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { ArrowImportRegular } from '@fluentui/react-icons';
import { EmptyState } from '../ui';
import { DecomposePreviewDialog } from '../DecomposePreviewDialog';
import { WorkspaceFilePicker } from '../WorkspaceFilePicker';
import { CaptureTaskForm } from './CaptureTaskForm';
import { columnAccentColor,
  fixedBoardColumns } from './columnMeta';
import { KanbanColumn } from './KanbanColumn';
import { PickupSettings } from './PickupSettings';
import { useCtrlScrollZoom,
  ZoomControls } from './useCtrlScrollZoom';
import { useCallback, useMemo, useState } from 'react';
import type { ProposedBacklogItem } from '../../api/types';
const useStyles = makeStyles({
  boardRoot: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  intakeSection: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    alignItems: 'stretch',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    borderTopWidth: '1px',
    borderRightWidth: '1px',
    borderBottomWidth: '1px',
    borderLeftWidth: '1px',
    borderTopStyle: 'solid',
    borderRightStyle: 'solid',
    borderBottomStyle: 'solid',
    borderLeftStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    boxShadow: tokens.shadow4,
    '@media (max-width: 900px)': {
      gridTemplateColumns: '1fr',
    },
  },
  intakeMain: {
    display: 'grid',
    gridTemplateColumns: '170px minmax(320px, 1fr)',
    gap: tokens.spacingHorizontalM,
    alignItems: 'start',
    '@media (max-width: 900px)': {
      gridTemplateColumns: '1fr',
    },
  },
  intakeCopy: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    paddingTop: tokens.spacingVerticalXS,
  },
  capture: {
    minWidth: '280px',
  },
  toolbarActions: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    alignContent: 'center',
    gap: tokens.spacingHorizontalS,
    justifyContent: 'flex-end',
  },
  workflowSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionHeader: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    justifyContent: 'space-between',
  },
  embeddedHeader: {
    flex: '1 1 auto',
    padding: 0,
  },
  embeddedHeaderBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  embeddedTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  embeddedSubtitle: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  workflowTitleBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  summaryStrip: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  summaryButton: {
    minWidth: 'unset',
    height: '24px',
    paddingRight: tokens.spacingHorizontalS,
    paddingLeft: tokens.spacingHorizontalS,
    borderRadius: tokens.borderRadiusMedium,
    fontVariantNumeric: 'tabular-nums',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontWeight: tokens.fontWeightRegular,
  },
  summaryButtonSubtle: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
    color: tokens.colorNeutralForeground2,
  },
  summaryButtonInfo: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderTopColor: tokens.colorNeutralStrokeAccessible,
    borderRightColor: tokens.colorNeutralStrokeAccessible,
    borderBottomColor: tokens.colorNeutralStrokeAccessible,
    borderLeftColor: tokens.colorNeutralStrokeAccessible,
    color: tokens.colorNeutralForeground1,
  },
  summaryButtonWarning: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderTopColor: tokens.colorStatusWarningBorder1,
    borderRightColor: tokens.colorStatusWarningBorder1,
    borderBottomColor: tokens.colorStatusWarningBorder1,
    borderLeftColor: tokens.colorStatusWarningBorder1,
    color: tokens.colorStatusWarningForeground1,
  },
  summaryButtonDanger: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderTopColor: tokens.colorStatusDangerBorder1,
    borderRightColor: tokens.colorStatusDangerBorder1,
    borderBottomColor: tokens.colorStatusDangerBorder1,
    borderLeftColor: tokens.colorStatusDangerBorder1,
    color: tokens.colorStatusDangerForeground1,
  },
  boardEmpty: {
    marginTop: tokens.spacingVerticalM,
    maxWidth: '72ch',
  },
  boardSurface: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    borderTopWidth: '1px',
    borderRightWidth: '1px',
    borderBottomWidth: '1px',
    borderLeftWidth: '1px',
    borderTopStyle: 'solid',
    borderRightStyle: 'solid',
    borderBottomStyle: 'solid',
    borderLeftStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
  },
  columnsViewport: {
    overflowX: 'auto',
    paddingBottom: tokens.spacingVerticalS,
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(220px, 1fr))',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    width: '100%',
    // Zoom origin: anchor to the top-left so zooming out keeps Backlog in place.
    transformOrigin: 'top left',
  },
  mainColumn: {
    minWidth: 0,
  },
  problemsSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    borderTopWidth: '1px',
    borderRightWidth: '1px',
    borderBottomWidth: '1px',
    borderLeftWidth: '1px',
    borderTopStyle: 'solid',
    borderRightStyle: 'solid',
    borderBottomStyle: 'solid',
    borderLeftStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  problemsColumns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(240px, 0.85fr) minmax(320px, 1.35fr)',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    overflowX: 'auto',
    paddingBottom: tokens.spacingVerticalXS,
    '@media (max-width: 900px)': {
      gridTemplateColumns: '1fr',
    },
  },
  attentionColumn: {
    minWidth: 0,
  },
});

export interface KanbanBoardProps {
  projectId: string;
  // Test seam: shorten the poll interval to avoid timing flakiness.
  pollIntervalMs?: number;
}

// Per-project homepage Kanban board. The board API remains stage-aware, but the UI
// presents a stable six-bucket view with an executable main flow
// (Backlog, Ready, Active, Done) plus Human Review and Problems grouped in a
// separate Needs attention / review section. Drag is constrained to Backlog<->Ready
// by the column drop handlers and the server (workflow columns never accept a task move).
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

  const jumpToBoardTarget = useCallback((targetId: string) => {
    const target = document.getElementById(targetId);
    if (!target) return;
    const prefersReducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false;
    target.scrollIntoView({
      behavior: prefersReducedMotion ? 'auto' : 'smooth',
      block: 'start',
      inline: 'nearest',
    });
    target.focus({ preventScroll: true });
  }, []);

  const visibleColumns = useMemo(() => (board ? fixedBoardColumns(board.columns) : []), [board]);

  // Deterministic accent palette keyed by each visible fixed column.
  const columnsWithAccent = useMemo(() => {
    let workflowIndex = 0;
    return visibleColumns.map((col) => ({
      col,
      accent: columnAccentColor(col.id, col.id === 'backlog' || col.id === 'ready' ? 0 : workflowIndex++),
    }));
  }, [visibleColumns]);

  const mainColumns = useMemo(() => columnsWithAccent.filter(({ col }) => col.id !== 'human-review' && col.id !== 'problems'), [columnsWithAccent]);
  const attentionColumns = useMemo(() => columnsWithAccent.filter(({ col }) => col.id === 'human-review' || col.id === 'problems'), [columnsWithAccent]);
  const boardSummary = useMemo(() => {
    const cards = columnsWithAccent.flatMap(({ col }) => col.cards);
    const queuedTasks = cards.filter((card) => card.kind === 'task').length;
    const activeRuns = mainColumns
      .filter(({ col }) => col.id === 'active')
      .reduce((sum, { col }) => sum + col.cards.filter((card) => card.kind === 'run').length, 0);
    const approvals = cards.filter((card) => card.kind === 'run' && card.has_pending_approval).length;
    const needsAttention = attentionColumns.reduce((sum, { col }) => sum + col.cards.length, 0);
    return { queuedTasks, activeRuns, approvals, needsAttention, total: cards.length };
  }, [attentionColumns, columnsWithAccent, mainColumns]);

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
        setRejectMessage('Workflow states are controlled by agent execution. Move tasks between Backlog and Ready; the coordinator advances runs after pickup.');
        return;
      }
      await refetch();
    } catch (e) {
      setMutationError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
    }
  };

  const handleRejectDrop = () => {
    setDraggingTaskId(null);
    setRejectMessage('Workflow states are controlled by agent execution. Move tasks between Backlog and Ready; the coordinator advances runs after pickup.');
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
    <div className={styles.boardRoot}>
      <section
        className={styles.intakeSection}
        aria-labelledby="board-intake-title"
      >
        <div className={styles.intakeMain}>
          <div className={styles.intakeCopy}>
            <div className={mergeClasses(styles.embeddedHeader, styles.embeddedHeaderBlock)}>
              <Text as="h2" id="board-intake-title" className={styles.embeddedTitle}>Intake</Text>
              <Text className={styles.embeddedSubtitle}>Capture agent-executable work, import tasks from a spec, or tune automatic pickup.</Text>
            </div>
          </div>
          <div className={styles.capture}>
            <CaptureTaskForm projectId={projectId} onCaptured={refetch} />
          </div>
        </div>
        <div role="toolbar" aria-label="Intake commands" className={styles.toolbarActions}>
          <Button
            appearance="secondary"
            icon={<ArrowImportRegular />}
            onClick={() => { setImportSelectedPath(null); setImportPickerOpen(true); setImportSuccess(false); }}
          >
            Import from workspace
          </Button>
          <PickupSettings projectId={projectId} />
        </div>
      </section>

      {importSuccess && (
        <MessageBar intent="success">
          <MessageBarBody>Tasks imported to Backlog for orchestration.</MessageBarBody>
        </MessageBar>
      )}

      {status === 'loading' && !board && <Spinner label="Loading orchestration board" />}

      {status === 'error' && error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {board && !board.workflow_stages_available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Workflow state is temporarily unavailable. Intake tasks still show, but live run buckets may be incomplete until the API recovers.
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
        <section className={styles.workflowSection} aria-labelledby="board-workflow-title">
          <div className={styles.sectionHeader}>
            <div className={styles.workflowTitleBlock}>
              <div className={mergeClasses(styles.embeddedHeader, styles.embeddedHeaderBlock)}>
                <Text as="h2" id="board-workflow-title" className={styles.embeddedTitle}>Agent task board</Text>
                <Text className={styles.embeddedSubtitle}>Fixed states show the autonomous flow from intake through completion.</Text>
              </div>
              <div className={styles.summaryStrip} aria-label="Board execution summary">
                <Button
                  appearance="secondary"
                  size="small"
                  className={mergeClasses(styles.summaryButton, styles.summaryButtonSubtle)}
                  aria-label={`Jump to Backlog: ${boardSummary.queuedTasks} queued tasks`}
                  onClick={() => jumpToBoardTarget('board-column-backlog')}
                >
                  {boardSummary.queuedTasks} queued tasks
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  className={mergeClasses(styles.summaryButton, styles.summaryButtonInfo)}
                  aria-label={`Jump to Active: ${boardSummary.activeRuns} active runs`}
                  onClick={() => jumpToBoardTarget('board-column-active')}
                >
                  {boardSummary.activeRuns} active runs
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  className={mergeClasses(styles.summaryButton, boardSummary.approvals ? styles.summaryButtonWarning : styles.summaryButtonSubtle)}
                  aria-label={`Jump to Human Review: ${boardSummary.approvals} approvals`}
                  onClick={() => jumpToBoardTarget('board-column-human-review')}
                >
                  {boardSummary.approvals} approvals
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  className={mergeClasses(styles.summaryButton, boardSummary.needsAttention ? styles.summaryButtonDanger : styles.summaryButtonSubtle)}
                  aria-label={`Jump to Needs attention / review: ${boardSummary.needsAttention} items need attention`}
                  onClick={() => jumpToBoardTarget('board-attention-section')}
                >
                  {boardSummary.needsAttention} needs attention
                </Button>
              </div>
            </div>
            <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
          </div>
          <div className={styles.boardSurface}>
            <div className={styles.columnsViewport} ref={viewportRef}>
              <div className={styles.columns} style={{ zoom }}>
                {mainColumns.map(({ col: column, accent }) => (
                  <KanbanColumn
                    key={column.id}
                    column={column}
                    className={styles.mainColumn}
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
            {boardSummary.total === 0 && (
              <EmptyState
                className={styles.boardEmpty}
                title="No orchestration tasks yet."
                description="Capture a task or import a spec to seed Backlog, then move committed work to Ready for agent pickup."
              />
            )}
          </div>

          {attentionColumns.length > 0 && (
            <section
              id="board-attention-section"
              tabIndex={-1}
              className={styles.problemsSection}
              aria-labelledby="board-attention-title"
            >
              <div className={mergeClasses(styles.embeddedHeader, styles.embeddedHeaderBlock)}>
                <Text as="h2" id="board-attention-title" className={styles.embeddedTitle}>Needs attention / review</Text>
                <Text className={styles.embeddedSubtitle}>Human review, failed, or blocked runs are separated from the main flow for quick intervention.</Text>
              </div>
              <div className={styles.problemsColumns}>
                {attentionColumns.map(({ col: column, accent }) => (
                  <KanbanColumn
                    key={column.id}
                    column={column}
                    className={styles.attentionColumn}
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
            </section>
          )}
        </section>
      )}
      {board && columnsWithAccent.length === 0 && (
        <EmptyState title="No board states are available yet." />
      )}

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

