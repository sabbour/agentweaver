/**
 * Project Board in the 0.x layout: an Intake section with the capture form,
 * then the agent task board with Backlog, Ready, Active and Done columns.
 * Tasks come from the project backlog graph. Synced repository issues add the
 * issue reference and state to the cards that track them. Task content can be
 * edited in place. Queue state and prerequisite edges use revision-checked
 * owner operations; cards are not draggable.
 */
import {
  Badge,
  Button,
  Caption1,
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
import { ArrowSyncRegular, CheckmarkRegular, DismissRegular, EditRegular } from '@fluentui/react-icons';
import { useCallback, useMemo, useState } from 'react';
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
  archiveTask,
  addPrerequisite,
  removePrerequisite,
  isStaleRevision,
  MOVE_UNAVAILABLE_COPY,
  prerequisiteTitles,
  useProjectBoard,
} from './boardData';
import type { BoardColumn } from './boardData';
import type { BacklogGraph, BacklogTaskSnapshot, ProjectIssueSummary } from './contracts';
import { Loading, PageHeading } from './pageParts';
import { permissionsForProject, useAuthorizationContext } from './projectData';

/** Route state set by Start task after it adds a task, so the Board can confirm it. */
export interface BoardLocationState {
  capturedTaskTitle?: string;
}

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

/** Props for one task card. */
interface BoardTaskCardProps {
  graph: BacklogGraph;
  task: BacklogTaskSnapshot;
  issue: ProjectIssueSummary | undefined;
  canEdit: boolean;
  onSaved: (graph: BacklogGraph, message: string) => void;
  onConflict: () => void;
}

/**
 * One task card. It shows the title, description, prerequisites, linked issue
 * and queue state. Users with edit access can update task content; queue and
 * prerequisite actions are available only while the task is unclaimed.
 */
function BoardTaskCard({ graph, task, issue, canEdit, onSaved, onConflict }: BoardTaskCardProps) {
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
  // The service rejects edits while an automation invocation is pending, so the
  // card hides the edit button then instead of offering an action that fails.
  const editable = canEdit && !task.isArchived && !task.isAutomationInvocationPending;
  const queueEditable = editable && (task.state === 'backlog' || task.state === 'ready');
  const canChangeQueue = queueEditable && task.lifecycle === 'open';

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
      <div className={styles.card} data-testid={`task-card-${task.reference.taskId}`}>
        <div className={styles.editFields}>
          <Field label="Title" required>
            <Input value={title} maxLength={512} onChange={(_, value) => setTitle(value.value)} disabled={busy} />
          </Field>
          <Field label="Description">
            <Textarea value={description} maxLength={65536} onChange={(_, value) => setDescription(value.value)} disabled={busy} rows={3} />
          </Field>
          {error && <Text className={styles.error}>{error}</Text>}
          <div className={styles.editActions}>
            <Button
              appearance="secondary"
              size="small"
              icon={<DismissRegular />}
              disabled={busy}
              onClick={() => { setEditing(false); setError(null); }}
            >
              Cancel
            </Button>
            <Button appearance="primary" size="small" icon={<CheckmarkRegular />} disabled={busy || !title.trim()} onClick={() => void save()}>
              Save
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.card} data-testid={`task-card-${task.reference.taskId}`}>
      <div className={styles.cardHeader}>
        <Text className={styles.cardTitle}>{task.title}</Text>
        {queueEditable && (
          <Button appearance="subtle" size="small" icon={<EditRegular />} aria-label={`Edit task ${task.title}`} onClick={startEdit} />
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
                    disabled={busy}
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
                disabled={busy}
                onClick={() => void mutate(
                  () => setTaskState(apiCall, graph, task, task.state === 'ready' ? 'backlog' : 'ready'),
                  task.state === 'ready' ? 'Task moved to Backlog.' : 'Task marked Ready.',
                )}
              >
                {task.state === 'ready' ? 'Move to Backlog' : 'Mark Ready'}
              </Button>
            )}
            <Button
              appearance="subtle"
              size="small"
              disabled={busy}
              onClick={() => void mutate(() => archiveTask(apiCall, graph, task), 'Task archived.')}
            >
              Archive
            </Button>
          </div>
          {graph.tasks.some((item) =>
            item.reference.taskId !== task.reference.taskId &&
            !item.isArchived &&
            !item.isAutomationInvocationPending &&
            !dependencyEdges.some((edge) => edge.prerequisite.taskId === item.reference.taskId)) && (
            <div className={styles.dependencyControls}>
              <Field label="Add prerequisite" className={styles.dependencySelect}>
                <Select
                  value={prerequisiteTaskId}
                  disabled={busy}
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
                disabled={busy || !prerequisiteTaskId}
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
  onSaved: (graph: BacklogGraph, message: string) => void;
  onConflict: () => void;
}

/** One column with the 0.x header: accent bar, label, count, caption and queue summary. */
function BoardColumnView({ column, graph, issues, canEdit, onSaved, onConflict }: BoardColumnViewProps) {
  const styles = useStyles();
  const accent = columnAccentColor(column.id, 0);
  const count = column.tasks.length;
  return (
    <section
      id={`board-column-${column.id}`}
      className={styles.column}
      tabIndex={-1}
      aria-label={`${column.label} column`}
      data-testid={`column-${column.id}`}
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
          {column.tasks.map((task) => (
            <BoardTaskCard
              // The revision is part of the key so a saved or reloaded task resets its local draft.
              key={`${task.reference.taskId}:${task.revision}`}
              graph={graph}
              task={task}
              issue={issues.get(task.reference.taskId)}
              canEdit={canEdit}
              onSaved={onSaved}
              onConflict={onConflict}
            />
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

/** Renders one project's Board. */
export function BoardPage() {
  const styles = useStyles();
  const { projectId = '' } = useParams();
  const location = useLocation();
  const { apiCall } = useAuth();
  // Each navigation to the Board, including the one after Start task adds a
  // task, has a new location key. Passing it reloads the board on arrival.
  const board = useProjectBoard(projectId, location.key);
  const authorization = useAuthorizationContext();
  const { zoom, zoomIn, zoomOut, viewportRef } = useCtrlScrollZoom();
  const [notice, setNotice] = useState<string | null>(null);
  const captured = (location.state as BoardLocationState | null)?.capturedTaskTitle;

  // Capture and edit need write access. Until the access check loads, or if it
  // fails, the write controls stay hidden; the service still enforces access.
  const canWrite = useMemo(() => !!authorization.data
    && permissionsForProject(authorization.data, projectId).some((item) => item.permission === 'writeProjects'),
  [authorization.data, projectId]);
  const columns = useMemo(() => (board.graph ? boardColumns(board.graph) : []), [board.graph]);
  const visibleCount = columns.reduce((sum, column) => sum + column.tasks.length, 0);
  const countOf = (id: string) => columns.find((column) => column.id === id)?.tasks.length ?? 0;
  const { reload, replaceGraph } = board;
  const graphRevision = board.graph?.revision;

  // Called by the capture form. Errors are rethrown as plain messages because the form shows `error.message`.
  const capture = useCallback(async (title: string) => {
    setNotice(null);
    try {
      const next = await captureTask(apiCall, projectId, title, graphRevision);
      replaceGraph(next);
      setNotice('Task added to Backlog.');
    } catch (reason) {
      // A conflict that survived the retry means the board is stale; reload it.
      if (isStaleRevision(reason)) reload();
      throw new Error(boardErrorMessage(reason, 'capture'), { cause: reason });
    }
  }, [apiCall, graphRevision, projectId, reload, replaceGraph]);

  const onSaved = useCallback((next: BacklogGraph, message: string) => {
    replaceGraph(next);
    setNotice(message);
  }, [replaceGraph]);

  const readError = board.error;
  const forbidden = readError instanceof GatewayError && readError.status === 403;

  return (
    <div className={styles.root}>
      <PageHeading
        title="Board"
        description="Queue work and record prerequisites; a run checks live eligibility when it claims a task."
        page="Board"
        actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={board.loading} onClick={reload}>Refresh</Button>}
      />

      {/* The capture form is shown only to users who can write, and only once the board has loaded. */}
      {board.graph && canWrite && (
        <section className={styles.intakeSection} aria-labelledby="board-intake-title">
          <div className={styles.intakeMain}>
            <div className={styles.intakeCopy}>
              <Text as="h2" id="board-intake-title" className={styles.sectionTitle}>Intake</Text>
              <Text className={styles.sectionSubtitle}>Capture agent-executable work into Backlog.</Text>
            </div>
            <div className={styles.capture}>
              <CaptureTaskForm projectId={projectId} capture={capture} onCaptured={() => undefined} disabled={board.loading} />
            </div>
          </div>
        </section>
      )}

      {captured && !notice && (
        <MessageBar intent="success"><MessageBarBody>{`Added "${captured}" to Backlog.`}</MessageBarBody></MessageBar>
      )}
      {notice && <MessageBar intent="success"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}

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
              <Text as="h2" id="board-workflow-title" className={styles.sectionTitle}>Agent task board</Text>
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
              <Text className={styles.moveNote} data-testid="board-move-unavailable">{MOVE_UNAVAILABLE_COPY}</Text>
            </div>
            <ZoomControls zoom={zoom} onZoomIn={zoomIn} onZoomOut={zoomOut} />
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
                    onSaved={onSaved}
                    onConflict={reload}
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
    </div>
  );
}
