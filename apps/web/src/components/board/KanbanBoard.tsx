import { useCallback, useMemo, useState } from 'react';
import {
  Button,
  Caption1,
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
  mergeClasses,
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
    gap: tokens.spacingVerticalXL,
  },
  intakeSection: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    alignItems: 'stretch',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow2,
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
    gap: tokens.spacingVerticalXXS,
    paddingTop: tokens.spacingVerticalXS,
  },
  intakeTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  intakeHelp: {
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '42ch',
  },
  capture: {
    minWidth: '280px',
  },
  toolbarActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
  },
  workflowSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  sectionTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  sectionTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    overflowWrap: 'anywhere',
  },
  sectionDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '70ch',
    overflowWrap: 'anywhere',
  },
  summaryStrip: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
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
    borderTopColor: tokens.colorBrandStroke2,
    borderRightColor: tokens.colorBrandStroke2,
    borderBottomColor: tokens.colorBrandStroke2,
    borderLeftColor: tokens.colorBrandStroke2,
    color: tokens.colorBrandForeground1,
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
    padding: tokens.spacingVerticalM,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground2,
    backgroundColor: tokens.colorNeutralBackground1,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '72ch',
  },
  boardSurface: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
  },
  columnsViewport: {
    overflowX: 'auto',
    paddingBottom: tokens.spacingVerticalS,
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalS,
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
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  problemsSectionTitle: {
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
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
    <div className={styles.root}>
      <section className={styles.intakeSection} aria-labelledby="board-intake-title">
        <div className={styles.intakeMain}>
          <div className={styles.intakeCopy}>
            <Text id="board-intake-title" className={styles.intakeTitle}>Intake</Text>
            <Caption1 className={styles.intakeHelp}>Capture agent-executable work, import tasks from a spec, or tune automatic pickup.</Caption1>
          </div>
          <div className={styles.capture}>
            <CaptureTaskForm projectId={projectId} onCaptured={refetch} />
          </div>
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
            <div className={styles.sectionTitleGroup}>
              <Text id="board-workflow-title" className={styles.sectionTitle}>Agent task board</Text>
              <Caption1 className={styles.sectionDescription}>Fixed Kanban states show the autonomous orchestration flow from intake through completion.</Caption1>
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
              <Text className={styles.boardEmpty}>No orchestration tasks yet. Capture a task or import a spec to seed Backlog, then move committed work to Ready for agent pickup.</Text>
            )}
          </div>

          {attentionColumns.length > 0 && (
            <section id="board-attention-section" tabIndex={-1} className={styles.problemsSection} aria-labelledby="board-attention-title">
              <div className={styles.sectionTitleGroup}>
                <Text id="board-attention-title" className={styles.problemsSectionTitle}>Needs attention / review</Text>
                <Caption1 className={styles.sectionDescription}>Human review, failed, or blocked runs are separated from the autonomous flow so operators can intervene quickly.</Caption1>
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
      {board && columnsWithAccent.length === 0 && <Text>No board states are available yet.</Text>}

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
