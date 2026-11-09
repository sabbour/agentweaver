/**
 * Project Board in the 0.x layout: an Intake section with the capture form,
 * then the agent task board with Backlog, Ready, Active and Done columns.
 * Tasks come from the project backlog graph. Synced repository issues add the
 * issue reference and state to the cards that track them. Task content can be
 * edited in place. Queue state, order and prerequisite edges use
 * revision-checked owner operations.
 */
import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Select,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, CheckmarkRegular, DeleteRegular, DismissRegular, EditRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { DragEvent } from 'react';
import { useLocation, useParams } from 'react-router-dom';
import { CaptureTaskForm } from '../components/board/CaptureTaskForm';
import { columnAccentColor } from '../components/board/columnMeta';
import { useCtrlScrollZoom, ZoomControls } from '../components/board/useCtrlScrollZoom';
import { EmptyState, ErrorState } from '../components/ui/States';
import { GatewayError } from './api';
import { useAuth } from './AuthContext';
import {
  boardColumns,
  boardErrorMessage,
  captureTask,
  editTaskContent,
  setTaskState,
  setTaskPosition,
  setAllEligibleTasksReady,
  archiveTask,
  deleteBacklogTask,
  BoardScopeChangedError,
  addPrerequisite,
  removePrerequisite,
  isStaleRevision,
  MOVE_GUIDANCE_COPY,
  queueDropPosition,
  prerequisiteTitles,
  queueInsertionPosition,
  useProjectBoard,
} from './boardData';
import type { BoardRequestScope } from './boardData';
import type { BoardColumn } from './boardData';
import type { BacklogGraph, BacklogTaskSnapshot, ProjectIssueSummary } from './contracts';
import { ErrorNotice, Loading, PageHeading, Panel } from './pageParts';
import { permissionsForProject, useAuthorizationContext } from './projectData';
import { metricTiles, metricsErrorMessage, useProjectMetrics } from './projectMetrics';
import { RunList } from './RunList';
import { runListErrorMessage, useProjectRunList } from './runListData';

/** Route state set by Start task after it adds a task, so the Board can confirm it. */
export interface BoardLocationState {
  capturedTaskTitle?: string;
}

interface BoardFeedback {
  scope: BoardRequestScope;
  notice: string | null;
  actionError: string | null;
  deleteError: string | null;
  batchReadyBusy: boolean;
}

interface BoardDragSnapshot {
  taskId: string;
  graphRevision: number;
  taskRevision: number;
  scope: BoardRequestScope;
}

type QueueState = 'backlog' | 'ready';
type QueueDropPlacement = 'before' | 'after';

const border = (color: string) => ({
  borderTopWidth: '1px',
  borderRightWidth: '1px',
  borderBottomWidth: '1px',
  borderLeftWidth: '1px',
  borderTopStyle: 'solid' as const,
  borderRightStyle: 'solid' as const,
  borderBottomStyle: 'solid' as const,
  borderLeftStyle: 'solid' as const,
  borderTopColor: color,
  borderRightColor: color,
  borderBottomColor: color,
  borderLeftColor: color,
});

// Styles follow the 0.x KanbanBoard, KanbanColumn and TaskCard so the board looks the same.
const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  intakeSection: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    alignItems: 'stretch',
    gap: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    ...border(tokens.colorNeutralStroke2),
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    boxShadow: tokens.shadow4,
  },
  intakeMain: {
    display: 'grid',
    gridTemplateColumns: '170px minmax(320px, 1fr)',
    gap: tokens.spacingHorizontalM,
    alignItems: 'start',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  intakeCopy: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, paddingTop: tokens.spacingVerticalXS },
  capture: { minWidth: '280px' },
  workflowSection: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  runMetricGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
    gap: tokens.spacingHorizontalS,
  },
  runMetric: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    ...border(tokens.colorNeutralStroke2),
  },
  runMetricLabel: { color: tokens.colorNeutralForeground2 },
  runMetricValue: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600, fontVariantNumeric: 'tabular-nums' },
  runMetricCaption: { color: tokens.colorNeutralForeground3 },
  runLists: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: tokens.spacingHorizontalL,
    '@media (max-width: 700px)': { gridTemplateColumns: '1fr' },
  },
  sectionHeader: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    justifyContent: 'space-between',
  },
  headerBlock: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  sectionTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400 },
  sectionSubtitle: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300, lineHeight: tokens.lineHeightBase300 },
  summaryStrip: { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS },
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
    backgroundColor: tokens.colorNeutralBackground1,
    ...border(tokens.colorNeutralStroke2),
    color: tokens.colorNeutralForeground2,
  },
  summaryButtonInfo: { ...border(tokens.colorNeutralStrokeAccessible), color: tokens.colorNeutralForeground1 },
  moveNote: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200 },
  cardActions: { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', gap: tokens.spacingHorizontalXS },
  dependencyRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  dependencyRow: { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  dependencyControls: { display: 'flex', flexDirection: 'row', alignItems: 'flex-end', gap: tokens.spacingHorizontalXS },
  dependencySelect: { minWidth: '160px' },
  boardSurface: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    ...border(tokens.colorNeutralStroke2),
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
  },
  columnsViewport: { overflowX: 'auto', paddingBottom: tokens.spacingVerticalS },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(220px, 1fr))',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    width: '100%',
    transformOrigin: 'top left',
  },
  boardEmpty: { marginTop: tokens.spacingVerticalM, maxWidth: '72ch' },
  column: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    width: '100%',
    minWidth: 0,
    alignSelf: 'flex-start',
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
  },
  columnDropTarget: {
    outline: `2px dashed ${tokens.colorBrandStroke1}`,
    outlineOffset: '2px',
  },
  columnHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
    padding: `${tokens.spacingVerticalS} 0 0`,
    borderTop: `3px solid ${tokens.colorNeutralStroke2}`,
  },
  titleRow: { display: 'flex', flexDirection: 'row', alignItems: 'center', gap: tokens.spacingHorizontalXS, minWidth: 0 },
  columnLabel: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    overflowWrap: 'anywhere',
    minWidth: 0,
  },
  countChip: { flexShrink: 0, fontVariantNumeric: 'tabular-nums' },
  columnDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    maxWidth: '34ch',
    overflowWrap: 'anywhere',
  },
  summaryRow: {
    display: 'flex',
    flexDirection: 'row',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    paddingTop: tokens.spacingVerticalXXS,
  },
  summaryText: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  cards: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  emptyColumn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '72px',
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground1,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    textAlign: 'center',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    ...border(tokens.colorNeutralStroke2),
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    transitionProperty: 'border-color, box-shadow, background-color',
    transitionDuration: tokens.durationFast,
    transitionTimingFunction: tokens.curveEasyEase,
    ':hover': {
      ...border(tokens.colorNeutralStroke1),
      boxShadow: tokens.shadow4,
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0ms' },
  },
  cardHeader: { display: 'flex', flexDirection: 'row', alignItems: 'flex-start', gap: tokens.spacingHorizontalXS, justifyContent: 'space-between' },
  draggableCard: { cursor: 'grab', ':active': { cursor: 'grabbing' } },
  draggingCard: { opacity: 0.55 },
  cardTitle: {
    flex: '1 1 auto',
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    overflowWrap: 'anywhere',
    minWidth: 0,
  },
  cardDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '65ch',
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
  },
  meta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  metadataRow: { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  editFields: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  editActions: { display: 'flex', flexDirection: 'row', alignItems: 'center', justifyContent: 'flex-end', gap: tokens.spacingHorizontalS },
  error: { color: tokens.colorPaletteRedForeground1, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200 },
  deleteAction: { color: tokens.colorPaletteRedForeground1 },
});

/** Short queue label for a card, matching the 0.x card wording where the state still exists. */
function queueLabel(task: BacklogTaskSnapshot): string {
  if (task.lifecycle === 'closed') return 'Closed';
  if (task.state === 'claimed') return 'Claimed by a run';
  return task.state === 'ready' ? 'Ready for a run' : 'Backlog intake';
}

/**
 * Text for the repository issue a task tracks. The synced issue summary is
 * preferred because it has the issue state. Without it, the link stored on
 * the task still names the issue.
 */
function issueReference(task: BacklogTaskSnapshot, issue: ProjectIssueSummary | undefined): { text: string; state?: 'open' | 'closed' } | null {
  if (issue) return { text: `${issue.repository.owner}/${issue.repository.name}#${issue.number}`, state: issue.state };
  const link = task.repositoryIssue;
  if (!link) return null;
  return { text: `${link.repository.owner}/${link.repository.name}#${link.issueNumber}` };
}

function taskDeleteUnavailableMessage(
  graph: BacklogGraph,
  task: BacklogTaskSnapshot,
  issue: ProjectIssueSummary | undefined,
): string | null {
  if (graph.dependencies.some((edge) =>
    edge.prerequisite.taskId === task.reference.taskId && edge.task.taskId !== task.reference.taskId))
    return 'Another task depends on this task. Remove its prerequisite link first.';
  if (task.repositoryIssue || issue)
    return "This task is linked to a repository issue and can't be deleted here.";
  return null;
}

function isTaskDeletionUnavailable(error: unknown): boolean {
  return error instanceof GatewayError
    && error.status === 409
    && Array.isArray(error.problem?.issues)
    && error.problem.issues.some((issue: unknown) =>
      !!issue && typeof issue === 'object'
      && typeof (issue as { code?: unknown }).code === 'string'
      && (issue as { code: string }).code.toLowerCase().replace(/[^a-z]/g, '') === 'taskdeletionunavailable');
}

function draggedTaskId(transfer: DataTransfer): string | null {
  const raw = transfer.getData('application/agentweaver-task');
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object' || !('taskId' in parsed)) return null;
    return typeof parsed.taskId === 'string' && parsed.taskId.length > 0 ? parsed.taskId : null;
  } catch {
    return null;
  }
}

/** Props for one task card. */
interface BoardTaskCardProps {
  graph: BacklogGraph;
  task: BacklogTaskSnapshot;
  earlierPosition: number | null;
  laterPosition: number | null;
  issue: ProjectIssueSummary | undefined;
  canEdit: boolean;
  canDelete: boolean;
  actionsDisabled: boolean;
  dragging: boolean;
  onDragStartTask: (task: BacklogTaskSnapshot) => boolean;
  onDragEndTask: () => void;
  deleteInFlight: boolean;
  onSaved: (graph: BacklogGraph, message: string) => void;
  onConflict: () => void;
  onDelete: (graph: BacklogGraph, task: BacklogTaskSnapshot, trigger: HTMLButtonElement) => void;
}

/**
 * One task card. It shows the title, description, prerequisites, linked issue
 * and queue state. Users with edit access can update task content; queue and
 * prerequisite actions are available only while the task is unclaimed.
 */
function BoardTaskCard({
  graph, task, earlierPosition, laterPosition, issue, canEdit, canDelete, actionsDisabled, deleteInFlight,
  dragging, onDragStartTask, onDragEndTask, onSaved, onConflict, onDelete,
}: BoardTaskCardProps) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(task.title);
  const [description, setDescription] = useState(task.description ?? '');
  const [prerequisiteTaskId, setPrerequisiteTaskId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const prerequisites = prerequisiteTitles(graph, task.reference.taskId);
  const dependencyEdges = graph.dependencies.filter((edge) => edge.task.taskId === task.reference.taskId);
  const reference = issueReference(task, issue);
  const deleteUnavailable = taskDeleteUnavailableMessage(graph, task, issue);
  const disabled = busy || actionsDisabled;
  // The service rejects edits while an automation invocation is pending, so the
  // card hides the edit button then instead of offering an action that fails.
  const editable = canEdit && !task.isArchived && !task.isAutomationInvocationPending;
  const queueEditable = editable && (task.state === 'backlog' || task.state === 'ready');
  const canChangeQueue = queueEditable && task.lifecycle === 'open';
  const canDrag = queueEditable && task.lifecycle === 'open' && !disabled && !editing;
  const onDragStart = (event: DragEvent<HTMLDivElement>) => {
    if (!canDrag) {
      event.preventDefault();
      return;
    }
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('application/agentweaver-task', JSON.stringify({ taskId: task.reference.taskId }));
    event.dataTransfer.setData('text/plain', task.reference.taskId);
    if (!onDragStartTask(task)) {
      event.preventDefault();
      onDragEndTask();
    }
  };
  const cardProps = {
    className: mergeClasses(styles.card, canDrag && styles.draggableCard, dragging && styles.draggingCard),
    'data-testid': `task-card-${task.reference.taskId}`,
    draggable: canDrag,
    onDragStart,
    onDragEnd: onDragEndTask,
  };

  const startEdit = () => {
    // Start from the latest saved content, not from an earlier cancelled draft.
    setTitle(task.title);
    setDescription(task.description ?? '');
    setError(null);
    setEditing(true);
  };

  const save = async () => {
    const trimmedTitle = title.trim();
    if (!trimmedTitle) {
      setError('Title is required.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      // An empty description is stored as null, the same as a task captured without one.
      const next = await editTaskContent(apiCall, graph, task, {
        title: trimmedTitle,
        description: description.trim() ? description.trim() : null,
      });
      setEditing(false);
      onSaved(next, 'Task saved.');
    } catch (reason) {
      setError(boardErrorMessage(reason, 'edit'));
      // A conflict or a missing task means the board is out of date. Reload it so
      // the next attempt uses fresh revisions. The draft stays for a retry.
      if (reason instanceof GatewayError && (reason.status === 409 || reason.status === 404)) onConflict();
    } finally {
      setBusy(false);
    }
  };

  const mutate = async (operation: () => Promise<BacklogGraph>, message: string) => {
    setBusy(true);
    setError(null);
    try {
      onSaved(await operation(), message);
      setPrerequisiteTaskId('');
    } catch (reason) {
      setError(boardErrorMessage(reason, 'mutate'));
      // A conflict or missing task makes this snapshot stale; reload before another action.
      if (reason instanceof GatewayError && (reason.status === 409 || reason.status === 404)) onConflict();
    } finally {
      setBusy(false);
    }
  };

  if (editing) {
    return (
      <div {...cardProps}>
        <div className={styles.editFields}>
          <Field label="Title" required>
            <Input value={title} maxLength={512} onChange={(_, value) => setTitle(value.value)} disabled={disabled} />
          </Field>
          <Field label="Description">
            <Textarea value={description} maxLength={65536} onChange={(_, value) => setDescription(value.value)} disabled={disabled} rows={3} />
          </Field>
          {error && <Text className={styles.error}>{error}</Text>}
          <div className={styles.editActions}>
            <Button
              appearance="secondary"
              size="small"
              icon={<DismissRegular />}
              disabled={disabled}
              onClick={() => { setEditing(false); setError(null); }}
            >
              Cancel
            </Button>
            <Button appearance="primary" size="small" icon={<CheckmarkRegular />} disabled={disabled || !title.trim()} onClick={() => void save()}>
              Save
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div {...cardProps}>
      <div className={styles.cardHeader}>
        <Text className={styles.cardTitle}>{task.title}</Text>
        {queueEditable && (
          <Button appearance="subtle" size="small" icon={<EditRegular />} aria-label={`Edit task ${task.title}`} disabled={disabled} onClick={startEdit} />
        )}
      </div>
      {task.description && <Text className={styles.cardDescription}>{task.description}</Text>}
      {task.isAutomationInvocationPending && (
        <div className={styles.metadataRow}>
          <Badge appearance="tint" color="informative">Automation running</Badge>
          <Caption1 className={styles.meta}>Editing is paused until it finishes.</Caption1>
        </div>
      )}
      {dependencyEdges.length > 0 && (
        <div className={styles.dependencyRows} aria-label={`Prerequisites for ${task.title}`}>
          {dependencyEdges.map((edge) => {
            const prerequisite = graph.tasks.find((item) => item.reference.taskId === edge.prerequisite.taskId);
            const title = prerequisite?.title || 'Task not shown';
            return (
              <div key={edge.prerequisite.taskId} className={styles.dependencyRow}>
                <Caption1 className={styles.meta}>Needs {title}</Caption1>
                {queueEditable && (
                  <Button
                    appearance="subtle"
                    size="small"
                    disabled={disabled}
                    aria-label={`Remove prerequisite ${title} from ${task.title}`}
                    onClick={() => void mutate(
                      () => removePrerequisite(apiCall, graph, task, edge.prerequisite.taskId),
                      'Prerequisite removed.',
                    )}
                  >
                    Remove
                  </Button>
                )}
              </div>
            );
          })}
        </div>
      )}
      {prerequisites.unknown > 0 && (
        <Caption1 className={styles.meta}>
          Needs {prerequisites.unknown} {prerequisites.unknown === 1 ? 'task' : 'tasks'} not shown on this board
        </Caption1>
      )}
      {reference && (
        <div className={styles.metadataRow}>
          <Caption1 className={styles.meta}>Issue {reference.text}</Caption1>
          {reference.state && (
            <Badge appearance="tint" color={reference.state === 'open' ? 'success' : 'subtle'} size="small">
              {reference.state === 'open' ? 'Open' : 'Closed'}
            </Badge>
          )}
        </div>
      )}
      <div className={styles.metadataRow}>
        <Text className={styles.meta}>{queueLabel(task)}</Text>
      </div>
      {queueEditable && (
        <>
          <div className={styles.cardActions}>
            {canChangeQueue && (
              <Button
                appearance="secondary"
                size="small"
                disabled={disabled}
                onClick={() => void mutate(
                  () => setTaskState(apiCall, graph, task, task.state === 'ready' ? 'backlog' : 'ready'),
                  task.state === 'ready' ? 'Task moved to Backlog.' : 'Task marked Ready.',
                )}
              >
                {task.state === 'ready' ? 'Move to Backlog' : 'Mark Ready'}
              </Button>
            )}
            {canChangeQueue && earlierPosition !== null && (
              <Button
                appearance="subtle"
                size="small"
                disabled={disabled}
                onClick={() => void mutate(
                  () => setTaskPosition(apiCall, graph, task,
                    task.state === 'ready' ? 'ready' : 'backlog', earlierPosition),
                  'Task moved earlier.',
                )}
              >
                Move earlier
              </Button>
            )}
            {canChangeQueue && laterPosition !== null && (
              <Button
                appearance="subtle"
                size="small"
                disabled={disabled}
                onClick={() => void mutate(
                  () => setTaskPosition(apiCall, graph, task,
                    task.state === 'ready' ? 'ready' : 'backlog', laterPosition),
                  'Task moved later.',
                )}
              >
                Move later
              </Button>
            )}
            <Button
              appearance="subtle"
              size="small"
              disabled={disabled}
              onClick={() => void mutate(() => archiveTask(apiCall, graph, task), 'Task archived.')}
            >
              Archive
            </Button>
            {canDelete && (
            <Button
              appearance="subtle"
              size="small"
              icon={<DeleteRegular />}
              className={styles.deleteAction}
              aria-label={`Delete task ${task.title}`}
              aria-describedby={deleteUnavailable ? `delete-unavailable-${task.reference.taskId}` : undefined}
              disabled={disabled || deleteInFlight || !!deleteUnavailable}
              onClick={(event) => onDelete(graph, task, event.currentTarget)}
            >
              Delete task
            </Button>
            )}
          </div>
          {canDelete && deleteUnavailable && (
            <Caption1 id={`delete-unavailable-${task.reference.taskId}`} className={styles.meta}>
            {deleteUnavailable}
            </Caption1>
          )}
          {graph.tasks.some((item) =>
            item.reference.taskId !== task.reference.taskId &&
            !item.isArchived &&
            !item.isAutomationInvocationPending &&
            !dependencyEdges.some((edge) => edge.prerequisite.taskId === item.reference.taskId)) && (
            <div className={styles.dependencyControls}>
              <Field label="Add prerequisite" className={styles.dependencySelect}>
                <Select
                  value={prerequisiteTaskId}
                  disabled={disabled}
                  onChange={(_, data) => setPrerequisiteTaskId(data.value)}
                >
                  <option value="">Choose a task</option>
                  {graph.tasks.filter((item) =>
                    item.reference.taskId !== task.reference.taskId &&
                    !item.isArchived &&
                    !item.isAutomationInvocationPending &&
                    !dependencyEdges.some((edge) => edge.prerequisite.taskId === item.reference.taskId))
                    .map((item) => (
                      <option key={item.reference.taskId} value={item.reference.taskId}>{item.title}</option>
                    ))}
                </Select>
              </Field>
              <Button
                appearance="secondary"
                size="small"
                disabled={disabled || !prerequisiteTaskId}
                onClick={() => void mutate(
                  () => addPrerequisite(apiCall, graph, task, prerequisiteTaskId),
                  'Prerequisite added.',
                )}
              >
                Add
              </Button>
            </div>
          )}
        </>
      )}
      {error && <Text className={styles.error}>{error}</Text>}
    </div>
  );
}

/** Props for one board column. */
interface BoardColumnViewProps {
  column: BoardColumn;
  graph: BacklogGraph;
  issues: ReadonlyMap<string, ProjectIssueSummary>;
  canEdit: boolean;
  canDelete: boolean;
  actionsDisabled: boolean;
  draggingTaskId: string | null;
  onDragStartTask: (task: BacklogTaskSnapshot) => boolean;
  onDragEndTask: () => void;
  onDropTask: (
    taskId: string,
    targetState: QueueState,
    neighborTaskId: string | null,
    placement: QueueDropPlacement,
  ) => void;
  deleteInFlight: boolean;
  onSaved: (graph: BacklogGraph, message: string) => void;
  onConflict: () => void;
  onDelete: (graph: BacklogGraph, task: BacklogTaskSnapshot, trigger: HTMLButtonElement) => void;
}

/** One column with the 0.x header: accent bar, label, count, caption and queue summary. */
function BoardColumnView({
  column, graph, issues, canEdit, canDelete, actionsDisabled, draggingTaskId, onDragStartTask, onDragEndTask,
  onDropTask, deleteInFlight, onSaved, onConflict, onDelete,
}: BoardColumnViewProps) {
  const styles = useStyles();
  const accent = columnAccentColor(column.id, 0);
  const count = column.tasks.length;
  const targetState: QueueState | null = column.id === 'backlog' || column.id === 'ready' ? column.id : null;
  const acceptsDrops = targetState !== null && canEdit && !actionsDisabled && draggingTaskId !== null;
  const [dragOver, setDragOver] = useState(false);
  const handleDragOver = acceptsDrops
    ? (event: DragEvent<HTMLElement>) => {
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      setDragOver(true);
    }
    : undefined;
  const handleDrop = (
    event: DragEvent<HTMLElement>,
    neighborTaskId: string | null,
    placement: QueueDropPlacement,
  ) => {
    if (!acceptsDrops || !targetState || !draggingTaskId) return;
    event.preventDefault();
    event.stopPropagation();
    setDragOver(false);
    const taskId = draggedTaskId(event.dataTransfer);
    if (taskId !== draggingTaskId) return;
    onDropTask(taskId, targetState, neighborTaskId, placement);
  };
  return (
    <section
      id={`board-column-${column.id}`}
      className={mergeClasses(styles.column, dragOver && styles.columnDropTarget)}
      tabIndex={-1}
      aria-label={`${column.label} column`}
      data-testid={`column-${column.id}`}
      onDragOver={handleDragOver}
      onDragLeave={() => setDragOver(false)}
      onDrop={acceptsDrops ? (event) => handleDrop(event, null, 'after') : undefined}
    >
      <div className={styles.columnHeader} style={{ borderTopColor: accent }}>
        <div className={styles.titleRow}>
          <Text className={styles.columnLabel}>{column.label}</Text>
          <Badge className={styles.countChip} appearance="tint" color="subtle" shape="rounded" size="small" data-testid={`count-${column.id}`}>
            {count}
          </Badge>
        </div>
        <Caption1 className={styles.columnDescription}>{column.description}</Caption1>
        <div className={styles.summaryRow} aria-label={`${column.label} summary`}>
          <Badge appearance="tint" color="subtle" size="small">Task queue</Badge>
          <Text className={styles.summaryText}>{`${count} ${count === 1 ? 'task' : 'tasks'}`}</Text>
        </div>
      </div>
      {count === 0 ? (
        <div className={styles.emptyColumn} data-testid={`empty-${column.id}`}>No tasks in this state.</div>
      ) : (
        <div className={styles.cards}>
          {column.tasks.map((task, visibleIndex) => (
            <div
              // The revision is part of the key so a saved or reloaded task resets its local draft.
              key={`${task.reference.taskId}:${task.revision}`}
              onDragOver={acceptsDrops ? handleDragOver : undefined}
              onDrop={acceptsDrops ? (event) => {
                const bounds = event.currentTarget.getBoundingClientRect();
                const placement = event.clientY < bounds.top + bounds.height / 2 ? 'before' : 'after';
                handleDrop(event, task.reference.taskId, placement);
              } : undefined}
            >
              <BoardTaskCard
                graph={graph}
                task={task}
                earlierPosition={queueInsertionPosition(graph, task, column.tasks[visibleIndex - 1], 'before')}
                laterPosition={queueInsertionPosition(graph, task, column.tasks[visibleIndex + 1], 'after')}
                issue={issues.get(task.reference.taskId)}
                canEdit={canEdit}
                canDelete={canDelete}
                actionsDisabled={actionsDisabled}
                dragging={draggingTaskId === task.reference.taskId}
                onDragStartTask={onDragStartTask}
                onDragEndTask={onDragEndTask}
                deleteInFlight={deleteInFlight}
                onSaved={onSaved}
                onConflict={onConflict}
                onDelete={onDelete}
              />
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

/** Moves focus and scroll to a board column, without smooth scrolling when the user prefers reduced motion. */
function jumpToColumn(id: string) {
  const target = document.getElementById(id);
  if (!target) return;
  const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false;
  target.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'start', inline: 'nearest' });
  target.focus({ preventScroll: true });
}

function RunListGroup({
  title,
  description,
  data,
  emptyText,
}: {
  title: string;
  description: string;
  data: ReturnType<typeof useProjectRunList>;
  emptyText: string;
}) {
  return (
    <div>
      {data.error != null && <ErrorNotice>{runListErrorMessage(data.error)}</ErrorNotice>}
      <RunList
        title={title}
        description={description}
        data={data}
        emptyText={data.loading ? 'Loading runs…' : data.error != null ? 'Run list could not be loaded.' : emptyText}
      />
    </div>
  );
}

function RunOverviewSection({ projectId, refreshKey }: { projectId: string; refreshKey: number }) {
  const styles = useStyles();
  const metrics = useProjectMetrics(projectId || null, '30d');
  const openRuns = useProjectRunList(projectId, { states: ['active', 'idle'] });
  const reviewRuns = useProjectRunList(projectId, { states: ['blocked'] });
  const problemRuns = useProjectRunList(projectId, { states: ['failed', 'indeterminate'] });
  const historyRuns = useProjectRunList(projectId, { states: ['terminal'] });
  const tiles = metricTiles(metrics.metrics, '30d', metrics.loading);
  const previousRefreshKey = useRef(refreshKey);

  useEffect(() => {
    if (refreshKey === previousRefreshKey.current) return;
    previousRefreshKey.current = refreshKey;
    void metrics.reload();
    openRuns.reload();
    reviewRuns.reload();
    problemRuns.reload();
    historyRuns.reload();
  }, [historyRuns.reload, metrics.reload, openRuns.reload, problemRuns.reload, refreshKey, reviewRuns.reload]);

  return (
    <Panel title="Run overview" description="Run counts and recent run history for this project.">
      {metrics.metricsError != null && (
        <ErrorNotice>{metricsErrorMessage(metrics.metricsError, 'run metrics')}</ErrorNotice>
      )}
      <div className={styles.runMetricGrid} aria-label="Run metrics">
        {tiles.map((tile) => (
          <div
            key={tile.key}
            className={styles.runMetric}
            data-testid={tile.key === 'active' ? 'board-active-run-count' : undefined}
          >
            <Text className={styles.runMetricLabel}>{tile.label}</Text>
            <Text className={styles.runMetricValue}>{tile.value}</Text>
            <Text className={styles.runMetricCaption}>{tile.meta}</Text>
          </div>
        ))}
      </div>
      <div className={styles.runLists}>
        <RunListGroup title="Open runs" description="Runs that are working or open." data={openRuns} emptyText="No open runs." />
        <RunListGroup title="Human review" description="Runs waiting for your decision or input." data={reviewRuns} emptyText="No runs need input." />
        <RunListGroup title="Problems" description="Runs that failed or have an unknown outcome." data={problemRuns} emptyText="No problem runs." />
        <RunListGroup title="Run history" description="Finished runs, newest first." data={historyRuns} emptyText="No finished runs." />
      </div>
    </Panel>
  );
}

/** Renders one project's Board. */
export function BoardPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const location = useLocation();
  const { apiCall, session, authorizationContext: confirmedAuthorization } = useAuth();
  const authorization = useAuthorizationContext();

  // Capture and edit need write access. Until the access check loads, or if it
  // fails, the write controls stay hidden; the service still enforces access.
  const canWrite = useMemo(() => !!authorization.data
    && permissionsForProject(authorization.data, projectId).some((item) => item.permission === 'writeProjects'),
  [authorization.data, projectId]);
  const authoritySignature = JSON.stringify(authorization.data?.effectiveAuthority ?? null);
  const scopeSignature = JSON.stringify([
    projectId,
    session?.binding?.projectId ?? null,
    session?.binding?.runId ?? null,
    confirmedAuthorization?.membershipRevision ?? null,
    authorization.data?.actorId ?? null,
    authorization.data?.tenantId ?? null,
    authorization.data?.membershipRevision ?? null,
    authoritySignature,
    canWrite,
  ]);
  const scope = useMemo<BoardRequestScope>(() => ({
    identity: { signature: scopeSignature },
    accessToken: session?.accessToken ?? null,
    tenantId: confirmedAuthorization?.tenantId ?? null,
  }), [
    scopeSignature,
    session?.accessToken,
    confirmedAuthorization?.tenantId,
  ]);
  const currentScopeRef = useRef<object | null>(null);
  const isScopeCurrent = useCallback((candidate: BoardRequestScope) =>
    currentScopeRef.current === candidate.identity, []);
  useLayoutEffect(() => {
    currentScopeRef.current = scope.identity;
    return () => {
      if (currentScopeRef.current === scope.identity) currentScopeRef.current = null;
    };
  }, [scope.identity]);
  // Each navigation to the Board, including the one after Start task adds a
  // task, has a new location key. Passing it reloads the board on arrival.
  const board = useProjectBoard(projectId, location.key, scope, isScopeCurrent);
  const [runOverviewRefreshKey, setRunOverviewRefreshKey] = useState(0);
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();
  const [feedback, setFeedback] = useState<BoardFeedback | null>(null);
  const activeFeedback = feedback?.scope === scope ? feedback : null;
  const notice = activeFeedback?.notice ?? null;
  const actionError = activeFeedback?.actionError ?? null;
  const deleteError = activeFeedback?.deleteError ?? null;
  const batchReadyBusy = activeFeedback?.batchReadyBusy ?? false;
  const updateFeedback = useCallback((updates: Partial<Omit<BoardFeedback, 'scope'>>) => {
    if (!isScopeCurrent(scope)) return;
    setFeedback((previous) => {
      if (!isScopeCurrent(scope)) return previous;
      const current = previous?.scope === scope
        ? previous
        : { scope, notice: null, actionError: null, deleteError: null, batchReadyBusy: false };
      return { ...current, ...updates };
    });
  }, [isScopeCurrent, scope]);
  const setNotice = useCallback((value: string | null) => updateFeedback({ notice: value }), [updateFeedback]);
  const setActionError = useCallback((value: string | null) => updateFeedback({ actionError: value }), [updateFeedback]);
  const setDeleteError = useCallback((value: string | null) => updateFeedback({ deleteError: value }), [updateFeedback]);
  const setBatchReadyBusy = useCallback((value: boolean) => updateFeedback({ batchReadyBusy: value }), [updateFeedback]);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    taskId: string;
    taskTitle: string;
    graphRevision: number;
    taskRevision: number;
    scope: BoardRequestScope;
  } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<{ taskId: string; scope: BoardRequestScope } | null>(null);
  const pendingDeleteRef = useRef<{ taskId: string; scope: BoardRequestScope } | null>(null);
  const [pendingMove, setPendingMove] = useState<{ taskId: string; scope: BoardRequestScope } | null>(null);
  const pendingMoveRef = useRef<{ taskId: string; scope: BoardRequestScope } | null>(null);
  const [drag, setDrag] = useState<BoardDragSnapshot | null>(null);
  const dragRef = useRef<BoardDragSnapshot | null>(null);
  const deleteTriggerRef = useRef<HTMLButtonElement | null>(null);
  const cancelDeleteRef = useRef<HTMLButtonElement | null>(null);
  const boardHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const focusHeadingAfterDelete = useRef<BoardRequestScope | null>(null);
  const captured = (location.state as BoardLocationState | null)?.capturedTaskTitle;
  const canDelete = canWrite
    && !!scope.accessToken
    && !!scope.tenantId
    && !!authorization.data?.actorId
    && authorization.data?.tenantId === scope.tenantId
    && authorization.data?.membershipRevision === confirmedAuthorization?.membershipRevision
    && !session?.binding;
  const activeConfirmation = deleteConfirmation?.scope === scope ? deleteConfirmation : null;
  const confirmationTask = activeConfirmation && board.graph
    ? board.graph.tasks.find((task) => task.reference.taskId === activeConfirmation.taskId)
    : undefined;
  const confirmationCurrent = !!activeConfirmation
    && !!board.graph
    && board.graph.revision === activeConfirmation.graphRevision
    && confirmationTask?.revision === activeConfirmation.taskRevision;
  const confirmationStale = !!activeConfirmation && !!board.graph && !confirmationCurrent;
  const pendingForScope = pendingDelete?.scope === scope;
  const pendingMoveForScope = pendingMove?.scope === scope;
  const activeDrag = drag?.scope === scope ? drag : null;
  const confirmationForScope = !!activeConfirmation && confirmationCurrent && canDelete;

  useLayoutEffect(() => {
    if (confirmationForScope) cancelDeleteRef.current?.focus();
  }, [confirmationForScope, deleteConfirmation?.scope]);
  useLayoutEffect(() => {
    if (!confirmationStale) return;
    boardHeadingRef.current?.focus();
  }, [confirmationStale]);
  const columns = useMemo(() => (board.graph ? boardColumns(board.graph) : []), [board.graph]);
  const visibleCount = columns.reduce((sum, column) => sum + column.tasks.length, 0);
  const countOf = (id: string) => columns.find((column) => column.id === id)?.tasks.length ?? 0;
  const { reload, replaceGraph } = board;
  const refreshBoard = useCallback(() => {
    reload();
    setRunOverviewRefreshKey((key) => key + 1);
  }, [reload]);
  const graphRevision = board.graph?.revision;

  // Called by the capture form. Errors are rethrown as plain messages because the form shows `error.message`.
  const capture = useCallback(async (title: string) => {
    const requestScope = scope;
    setNotice(null);
    try {
      const next = await captureTask(apiCall, projectId, title, graphRevision);
      if (!isScopeCurrent(requestScope)) return;
      replaceGraph(next);
      setNotice('Task added to Backlog.');
    } catch (reason) {
      // A conflict that survived the retry means the board is stale; reload it.
      if (isScopeCurrent(requestScope) && isStaleRevision(reason)) reload();
      throw new Error(boardErrorMessage(reason, 'capture'), { cause: reason });
    }
  }, [apiCall, graphRevision, isScopeCurrent, projectId, reload, replaceGraph, scope, setNotice]);

  const onSaved = useCallback((next: BacklogGraph, message: string) => {
    if (!isScopeCurrent(scope)) return;
    replaceGraph(next);
    setNotice(message);
    setActionError(null);
  }, [isScopeCurrent, replaceGraph, scope, setActionError, setNotice]);

  const onConflict = useCallback(() => {
    if (isScopeCurrent(scope)) reload();
  }, [isScopeCurrent, reload, scope]);

  const finishTaskDrag = useCallback(() => {
    dragRef.current = null;
    setDrag(null);
  }, []);

  const startTaskDrag = useCallback((task: BacklogTaskSnapshot) => {
    const graph = board.graph;
    const currentTask = graph?.tasks.find((candidate) => candidate.reference.taskId === task.reference.taskId);
    if (!graph || !currentTask || !canWrite || !isScopeCurrent(scope)
      || pendingDeleteRef.current?.scope === scope || pendingMoveRef.current || batchReadyBusy
      || currentTask.reference.projectId !== projectId || currentTask.revision !== task.revision
      || currentTask.isArchived || currentTask.isAutomationInvocationPending || currentTask.lifecycle !== 'open'
      || currentTask.state !== 'backlog' && currentTask.state !== 'ready') return false;
    const snapshot = {
      taskId: currentTask.reference.taskId,
      graphRevision: graph.revision,
      taskRevision: currentTask.revision,
      scope,
    };
    dragRef.current = snapshot;
    setDrag(snapshot);
    return true;
  }, [batchReadyBusy, board.graph, canWrite, isScopeCurrent, projectId, scope]);

  const moveTaskByDrop = useCallback(async (
    taskId: string,
    targetState: QueueState,
    neighborTaskId: string | null,
    placement: QueueDropPlacement,
  ) => {
    const requestScope = scope;
    const snapshot = dragRef.current;
    const graph = board.graph;
    const finish = () => {
      dragRef.current = null;
      setDrag(null);
    };
    if (!snapshot || snapshot.taskId !== taskId || snapshot.scope !== requestScope
      || !graph || !canWrite || pendingDeleteRef.current?.scope === requestScope
      || pendingMoveRef.current || batchReadyBusy || !isScopeCurrent(requestScope)) {
      finish();
      return;
    }

    const task = graph.tasks.find((candidate) => candidate.reference.taskId === taskId);
    if (!task || graph.revision !== snapshot.graphRevision || task.revision !== snapshot.taskRevision) {
      finish();
      setActionError('The board changed while you were dragging. It has been reloaded; try again.');
      reload();
      return;
    }
    if (task.reference.projectId !== projectId || task.isArchived || task.isAutomationInvocationPending
      || task.lifecycle !== 'open' || task.state !== 'backlog' && task.state !== 'ready') {
      finish();
      return;
    }

    const position = queueDropPosition(graph, taskId, targetState, neighborTaskId, placement);
    if (position === null) {
      finish();
      return;
    }
    const currentPosition = graph.tasks
      .filter((candidate) => candidate.state === task.state)
      .sort((left, right) =>
        left.position - right.position || left.reference.taskId.localeCompare(right.reference.taskId))
      .findIndex((candidate) => candidate.reference.taskId === taskId);
    if (targetState === task.state && position === currentPosition) {
      finish();
      return;
    }

    const pending = { taskId, scope: requestScope };
    pendingMoveRef.current = pending;
    setPendingMove(pending);
    finish();
    setActionError(null);
    setNotice(null);
    try {
      const next = await setTaskPosition(apiCall, graph, task, targetState, position, requestScope, isScopeCurrent);
      if (!isScopeCurrent(requestScope)) return;
      onSaved(next, targetState === task.state ? 'Task reordered.' : `Task moved to ${targetState === 'ready' ? 'Ready' : 'Backlog'}.`);
    } catch (reason) {
      if (!isScopeCurrent(requestScope)) return;
      setActionError(boardErrorMessage(reason, 'mutate'));
      if (reason instanceof BoardScopeChangedError
        || reason instanceof GatewayError && (reason.status === 404 || reason.status === 409)) reload();
    } finally {
      if (pendingMoveRef.current === pending) {
        pendingMoveRef.current = null;
        if (isScopeCurrent(requestScope)) setPendingMove(null);
      }
    }
  }, [
    apiCall,
    batchReadyBusy,
    board.graph,
    canWrite,
    isScopeCurrent,
    onSaved,
    projectId,
    reload,
    scope,
    setActionError,
    setNotice,
  ]);

  const openDeleteConfirmation = useCallback((
    graph: BacklogGraph,
    task: BacklogTaskSnapshot,
    trigger: HTMLButtonElement,
  ) => {
    if (!canDelete || pendingDeleteRef.current?.scope === scope || !isScopeCurrent(scope)) return;
    deleteTriggerRef.current = trigger;
    setDeleteError(null);
    setActionError(null);
    setNotice(null);
    setDeleteConfirmation({
      taskId: task.reference.taskId,
      taskTitle: task.title,
      graphRevision: graph.revision,
      taskRevision: task.revision,
      scope,
    });
  }, [canDelete, isScopeCurrent, scope, setActionError, setDeleteError, setNotice]);

  const closeDeleteConfirmation = useCallback(() => {
    const current = deleteConfirmation;
    if (!current || pendingDeleteRef.current?.scope === current.scope) return;
    setDeleteConfirmation(null);
    setDeleteError(null);
    const trigger = deleteTriggerRef.current;
    window.requestAnimationFrame(() => {
      if (isScopeCurrent(current.scope)) trigger?.focus();
    });
  }, [deleteConfirmation, isScopeCurrent, setDeleteError]);

  const confirmDelete = useCallback(async () => {
    const confirmation = deleteConfirmation;
    if (!confirmation || pendingDeleteRef.current?.scope === confirmation.scope || !canDelete
      || !isScopeCurrent(confirmation.scope)) return;
    const graph = board.graph;
    const task = graph?.tasks.find((candidate) => candidate.reference.taskId === confirmation.taskId);
    if (!graph || graph.revision !== confirmation.graphRevision || !task || task.revision !== confirmation.taskRevision) {
      if (isScopeCurrent(confirmation.scope)) {
        setDeleteConfirmation(null);
        setDeleteError(null);
        setActionError('The board changed while you were confirming. Review the task before deleting it.');
        reload();
      }
      return;
    }
    const unavailable = taskDeleteUnavailableMessage(graph, task, board.issues.get(task.reference.taskId));
    if (unavailable) {
      setDeleteError(unavailable);
      return;
    }

    const pending = { taskId: task.reference.taskId, scope: confirmation.scope };
    pendingDeleteRef.current = pending;
    setPendingDelete(pending);
    setDeleteError(null);
    setActionError(null);
    try {
      const next = await deleteBacklogTask(apiCall, graph, task, confirmation.scope, isScopeCurrent);
      if (!isScopeCurrent(confirmation.scope)) return;
      focusHeadingAfterDelete.current = confirmation.scope;
      deleteTriggerRef.current = null;
      replaceGraph(next);
      setDeleteConfirmation(null);
      setDeleteError(null);
      setNotice('Task deleted.');
    } catch (reason) {
      if (!isScopeCurrent(confirmation.scope)) return;
      const message = boardErrorMessage(reason, 'delete');
      if (reason instanceof BoardScopeChangedError
        || reason instanceof GatewayError && (reason.status === 404
          || reason.status === 409 && !isTaskDeletionUnavailable(reason))) {
        setDeleteConfirmation(null);
        setDeleteError(null);
        setActionError(message);
        reload();
      } else {
        setDeleteError(message);
      }
    } finally {
      if (pendingDeleteRef.current === pending) {
        pendingDeleteRef.current = null;
        setPendingDelete(null);
      }
    }
  }, [
    apiCall,
    board.graph,
    board.issues,
    canDelete,
    deleteConfirmation,
    isScopeCurrent,
    reload,
    replaceGraph,
    setActionError,
    setDeleteError,
    setNotice,
  ]);

  const sendAllToReady = useCallback(async () => {
    if (!board.graph) return;
    const requestScope = scope;
    setBatchReadyBusy(true);
    setActionError(null);
    try {
      onSaved(await setAllEligibleTasksReady(apiCall, board.graph), 'Eligible tasks moved to Ready.');
    } catch (reason) {
      if (!isScopeCurrent(requestScope)) return;
      setActionError(boardErrorMessage(reason, 'mutate'));
      if (reason instanceof GatewayError && (reason.status === 409 || reason.status === 404)) reload();
    } finally {
      if (isScopeCurrent(requestScope)) setBatchReadyBusy(false);
    }
  }, [apiCall, board.graph, isScopeCurrent, onSaved, reload, scope, setActionError, setBatchReadyBusy]);

  useLayoutEffect(() => {
    const expectedScope = focusHeadingAfterDelete.current;
    if (!expectedScope) return;
    focusHeadingAfterDelete.current = null;
    if (isScopeCurrent(expectedScope)) boardHeadingRef.current?.focus();
  }, [board.graph, isScopeCurrent]);

  const eligibleBacklogCount = board.graph?.tasks.filter((task) =>
    task.state === 'backlog' && task.lifecycle === 'open' &&
    !task.isArchived && !task.isAutomationInvocationPending).length ?? 0;
  const confirmationBlocker = confirmationTask && board.graph
    ? taskDeleteUnavailableMessage(board.graph, confirmationTask, board.issues.get(confirmationTask.reference.taskId))
    : null;
  const readError = board.error;
  const forbidden = readError instanceof GatewayError && readError.status === 403;

  return (
    <div className={styles.root}>
      <PageHeading
        title="Board"
        description="Queue work and record prerequisites; a run checks live eligibility when it claims a task."
        page="Board"
        actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={board.loading || pendingForScope || pendingMoveForScope} onClick={refreshBoard}>Refresh</Button>}
      />
      <RunOverviewSection projectId={projectId} refreshKey={runOverviewRefreshKey} />

      {/* The capture form is shown only to users who can write, and only once the board has loaded. */}
      {board.graph && canWrite && (
        <section className={styles.intakeSection} aria-labelledby="board-intake-title">
          <div className={styles.intakeMain}>
            <div className={styles.intakeCopy}>
              <Text as="h2" id="board-intake-title" className={styles.sectionTitle}>Intake</Text>
              <Text className={styles.sectionSubtitle}>Capture agent-executable work into Backlog.</Text>
            </div>
            <div className={styles.capture}>
              <CaptureTaskForm projectId={projectId} capture={capture} onCaptured={() => undefined} disabled={board.loading || pendingForScope} />
            </div>
          </div>
        </section>
      )}

      {captured && !notice && (
        <MessageBar intent="success"><MessageBarBody>{`Added "${captured}" to Backlog.`}</MessageBarBody></MessageBar>
      )}
      {notice && <MessageBar intent="success"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      {actionError && <MessageBar intent="error"><MessageBarBody>{actionError}</MessageBarBody></MessageBar>}
      {confirmationStale && (
        <MessageBar intent="warning">
          <MessageBarBody role="status">The board changed while you were confirming. Review the task before deleting it.</MessageBarBody>
        </MessageBar>
      )}

      {board.loading && !board.graph && <Loading label="Loading board" />}

      {/* A failed refresh keeps the last board on screen and shows the error above it. */}
      {readError != null && board.graph && (
        <MessageBar intent="error"><MessageBarBody>{boardErrorMessage(readError, 'read')}</MessageBarBody></MessageBar>
      )}
      {readError != null && !board.graph && (
        <ErrorState
          title={forbidden ? 'No access to this board' : "Couldn't load the board"}
          message={boardErrorMessage(readError, 'read')}
          onRetry={forbidden ? undefined : reload}
        />
      )}

      {board.graph && board.issuesError != null && (
        <MessageBar intent="warning">
          <MessageBarBody>Linked issue details couldn't be loaded, so cards show without issue state.</MessageBarBody>
        </MessageBar>
      )}
      {board.graph && board.issuesTruncated && (
        <MessageBar intent="warning">
          <MessageBarBody>This project has more linked issues than one load reads. Some cards show without issue state.</MessageBarBody>
        </MessageBar>
      )}

      {board.graph && (
        <section className={styles.workflowSection} aria-labelledby="board-workflow-title">
          <div className={styles.sectionHeader}>
            <div className={styles.headerBlock}>
              <Text as="h2" id="board-workflow-title" className={styles.sectionTitle} tabIndex={-1} ref={boardHeadingRef}>Agent task board</Text>
              <Text className={styles.sectionSubtitle}>Tasks grouped by queue state, from intake to completion.</Text>
              <div className={styles.summaryStrip} aria-label="Board summary">
                <Button
                  appearance="secondary"
                  size="small"
                  className={styles.summaryButton}
                  aria-label={`Jump to Backlog: ${countOf('backlog') + countOf('ready')} queued tasks`}
                  onClick={() => jumpToColumn('board-column-backlog')}
                >
                  {countOf('backlog') + countOf('ready')} queued tasks
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  className={mergeClasses(styles.summaryButton, styles.summaryButtonInfo)}
                  aria-label={`Jump to Active: ${countOf('active')} active tasks`}
                  onClick={() => jumpToColumn('board-column-active')}
                >
                  {countOf('active')} active tasks
                </Button>
                <Button
                  appearance="secondary"
                  size="small"
                  className={styles.summaryButton}
                  aria-label={`Jump to Done: ${countOf('done')} done tasks`}
                  onClick={() => jumpToColumn('board-column-done')}
                >
                  {countOf('done')} done
                </Button>
              </div>
              <Text className={styles.moveNote} data-testid="board-move-guidance">{MOVE_GUIDANCE_COPY}</Text>
            </div>
            <div className={styles.cardActions}>
              {canWrite && eligibleBacklogCount > 0 && (
                <Button
                  appearance="secondary"
                  size="small"
                  disabled={batchReadyBusy || board.loading || pendingForScope || pendingMoveForScope}
                  onClick={() => void sendAllToReady()}
                >
                  Send all to Ready
                </Button>
              )}
              <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
            </div>
          </div>
          <div className={styles.boardSurface}>
            <div className={styles.columnsViewport} ref={viewportRef}>
              <div className={styles.columns} style={{ zoom }}>
                {columns.map((column) => (
                  <BoardColumnView
                    key={column.id}
                    column={column}
                    graph={board.graph!}
                    issues={board.issues}
                    canEdit={canWrite}
                    canDelete={canDelete}
                    actionsDisabled={pendingForScope || pendingMoveForScope || batchReadyBusy}
                    draggingTaskId={activeDrag?.taskId ?? null}
                    onDragStartTask={startTaskDrag}
                    onDragEndTask={finishTaskDrag}
                    onDropTask={(taskId, targetState, neighborTaskId, placement) =>
                      void moveTaskByDrop(taskId, targetState, neighborTaskId, placement)}
                    deleteInFlight={pendingForScope}
                    onSaved={onSaved}
                    onConflict={onConflict}
                    onDelete={openDeleteConfirmation}
                  />
                ))}
              </div>
            </div>
            {visibleCount === 0 && (
              <EmptyState
                className={styles.boardEmpty}
                title="No tasks yet"
                description={canWrite
                  ? 'Capture a task in Intake or use Start task. New tasks appear in Backlog.'
                  : 'Tasks added to this project appear here.'}
              />
            )}
          </div>
        </section>
      )}

      <Dialog
        open={confirmationForScope}
        onOpenChange={(_, data) => {
          if (!data.open && pendingDeleteRef.current?.scope !== deleteConfirmation?.scope)
            closeDeleteConfirmation();
        }}
      >
        <DialogSurface aria-describedby="board-delete-dialog-description" aria-busy={pendingForScope || undefined}>
          <DialogBody>
            <DialogTitle>Delete task?</DialogTitle>
            <DialogContent>
              <Text as="p" id="board-delete-dialog-description">
                Delete “{confirmationTask?.title ?? deleteConfirmation?.taskTitle}”? This permanently removes the task and its prerequisite links. It does not delete a repository issue.
              </Text>
              {confirmationBlocker && (
                <MessageBar intent="warning"><MessageBarBody>{confirmationBlocker}</MessageBarBody></MessageBar>
              )}
              {deleteError && (
                <MessageBar intent="error"><MessageBarBody role="alert">{deleteError}</MessageBarBody></MessageBar>
              )}
              {pendingForScope && (
                <MessageBar intent="info"><MessageBarBody>Deleting task… This confirmation stays open until the request finishes.</MessageBarBody></MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" ref={cancelDeleteRef} disabled={pendingForScope} onClick={closeDeleteConfirmation}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                icon={<DeleteRegular />}
                className={styles.deleteAction}
                style={{ backgroundColor: tokens.colorPaletteRedBackground3 }}
                disabled={pendingForScope || !confirmationCurrent || !!confirmationBlocker}
                onClick={() => void confirmDelete()}
              >
                Delete task
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
