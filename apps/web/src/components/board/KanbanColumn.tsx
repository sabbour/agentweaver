import { Badge, Button, Caption1, Popover, PopoverSurface, PopoverTrigger, Text, Textarea, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { BoardColumnDto, RunCardDto, TaskCardDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { STAGE_DESCRIPTIONS } from './columnMeta';
import { TaskCard } from './TaskCard';
import { RunCard } from './RunCard';

const useStyles = makeStyles({
  column: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    flex: '0 0 300px',
    width: '300px',
    // Fit-content height — empty columns must not leave a giant vertical gap.
    alignSelf: 'flex-start',
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    // The 4px accent bar lives on the left edge; its color is applied inline.
    borderLeftWidth: '4px',
    borderLeftStyle: 'solid',
    boxShadow: tokens.shadow2,
  },
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  headerMain: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
    flex: 1,
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  countChip: {
    flexShrink: 0,
  },
  description: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    flexShrink: 0,
  },
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  dropzone: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '72px',
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textAlign: 'center',
  },
  dropzoneActive: {
    border: `1px dashed ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1Selected,
    color: tokens.colorNeutralForeground2,
  },
  addSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: '260px',
  },
  addActions: {
    display: 'flex',
    justifyContent: 'flex-end',
  },
  addTextarea: {
    width: '100%',
    resize: 'none',
  },
  addError: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

export interface KanbanColumnProps {
  column: BoardColumnDto;
  projectId: string;
  // Left-accent color for this column (palette mapping owned by KanbanBoard).
  accentColor: string;
  onMutated: () => void | Promise<void>;
  // Intake-only: a task card was dropped at targetIndex of this column.
  onDropTask: (taskId: string, sourceColumnId: string, targetColumnId: string, targetIndex: number) => void;
  // Workflow-only: a card was dropped onto a non-target column (FR-018).
  onRejectDrop: () => void;
  onDragStartTask: (taskId: string, sourceColumnId: string) => void;
  onDragEndTask: () => void;
  draggingTaskId: string | null;
  // Terminal column "Show older" toggle (FR-016a).
  includeTerminalHistory: boolean;
  onToggleTerminalHistory: () => void;
}

function parseDrag(e: React.DragEvent): { taskId: string; sourceColumnId: string } | null {
  const raw = e.dataTransfer.getData('application/agentweaver-task');
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as { taskId: string; sourceColumnId: string };
    if (parsed && typeof parsed.taskId === 'string') return parsed;
  } catch { /* ignore malformed payload */ }
  return null;
}

export function KanbanColumn(props: KanbanColumnProps) {
  const styles = useStyles();
  const {
    column, projectId, accentColor, onMutated, onDropTask, onRejectDrop,
    onDragStartTask, onDragEndTask, draggingTaskId,
    includeTerminalHistory, onToggleTerminalHistory,
  } = props;

  const [sendingAll, setSendingAll] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [addTitle, setAddTitle] = useState('');
  const [addBusy, setAddBusy] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const isIntake = column.kind === 'intake';
  const isTerminal = column.kind === 'workflow' && (column.id === 'terminal' || column.collapsed_count != null);
  const description = STAGE_DESCRIPTIONS[column.id];

  const handleSendAllToReady = async () => {
    setSendingAll(true);
    try {
      await apiClient.sendAllBacklogToReady(projectId);
      await onMutated();
    } finally {
      setSendingAll(false);
    }
  };

  // Per-column quick capture (FR-001/002). Backlog captures directly; Ready captures
  // into Backlog then promotes the new task so it lands in Ready. Empty/whitespace
  // titles are blocked client-side; the server is the backstop.
  const submitAdd = async () => {
    const trimmed = addTitle.trim();
    if (!trimmed) {
      setAddError('Title is required.');
      return;
    }
    setAddBusy(true);
    setAddError(null);
    try {
      const captured = await apiClient.captureBacklogTask(projectId, { title: trimmed });
      if (column.id === 'ready') {
        await apiClient.moveTaskToReady(projectId, captured.task_id);
      }
      setAddTitle('');
      setAddOpen(false);
      await onMutated();
    } catch (e) {
      setAddError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
    } finally {
      setAddBusy(false);
    }
  };

  // Intake columns are drop targets. Workflow columns are NOT — dropping there is
  // rejected (no API call) and surfaces a MessageBar via onRejectDrop.
  const intakeHandlers = isIntake
    ? {
        onDragOver: (e: React.DragEvent) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDragOver(true); },
        onDragLeave: () => setDragOver(false),
        onDrop: (e: React.DragEvent) => {
          e.preventDefault();
          setDragOver(false);
          const drag = parseDrag(e);
          if (drag) onDropTask(drag.taskId, drag.sourceColumnId, column.id, column.cards.length);
        },
      }
    : {
        // Workflow columns intentionally omit onDragOver (so the browser rejects the
        // drop / snaps the card back). The onDrop here only fires for synthetic drops
        // and never moves the task — it just explains the rejection.
        onDrop: (e: React.DragEvent) => {
          e.preventDefault();
          onRejectDrop();
        },
      };

  const handleCardDrop = (e: React.DragEvent, index: number) => {
    if (!isIntake) return;
    e.preventDefault();
    e.stopPropagation();
    const drag = parseDrag(e);
    if (drag) onDropTask(drag.taskId, drag.sourceColumnId, column.id, index);
  };

  return (
    <section
      className={styles.column}
      style={{ borderLeftColor: accentColor }}
      aria-label={`${column.label} column`}
      data-testid={`column-${column.id}`}
      data-column-kind={column.kind}
      data-accent-color={accentColor}
      {...intakeHandlers}
    >
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <div className={styles.titleRow}>
            <Text className={styles.label}>{column.label}</Text>
            <Badge
              className={styles.countChip}
              appearance="tint"
              color="informative"
              shape="rounded"
              size="small"
              data-testid={`count-${column.id}`}
            >
              {column.cards.length}
            </Badge>
          </div>
          {description && <Caption1 className={styles.description}>{description}</Caption1>}
        </div>
        <div className={styles.headerActions}>
          {column.id === 'backlog' && column.cards.length > 0 && (
            <Button
              appearance="subtle"
              size="small"
              disabled={sendingAll}
              onClick={handleSendAllToReady}
            >
              Send all to Ready
            </Button>
          )}
          {isIntake && (
            <Popover
              open={addOpen}
              trapFocus
              onOpenChange={(_, d) => {
                setAddOpen(d.open);
                if (!d.open) { setAddTitle(''); setAddError(null); }
              }}
            >
              <PopoverTrigger disableButtonEnhancement>
                <Button
                  appearance="subtle"
                  size="small"
                  icon={<AddRegular />}
                  aria-label={`Add to ${column.label}`}
                  title={`Add to ${column.label}`}
                />
              </PopoverTrigger>
              <PopoverSurface aria-label={`Add task to ${column.label}`}>
                <div className={styles.addSurface}>
                  <Textarea
                    className={styles.addTextarea}
                    value={addTitle}
                    placeholder={`Add a task to ${column.label}`}
                    aria-label={`New task title for ${column.label}`}
                    disabled={addBusy}
                    autoFocus
                    rows={3}
                    resize="none"
                    onChange={(_, v) => { setAddTitle(v.value); if (addError) setAddError(null); }}
                    onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submitAdd(); } }}
                  />
                  {addError && <Text className={styles.addError}>{addError}</Text>}
                  <div className={styles.addActions}>
                    <Button
                      appearance="primary"
                      icon={<AddRegular />}
                      disabled={addBusy || !addTitle.trim()}
                      onClick={() => void submitAdd()}
                    >
                      Add
                    </Button>
                  </div>
                </div>
              </PopoverSurface>
            </Popover>
          )}
        </div>
      </div>

      {column.cards.length === 0 ? (
        <div
          className={mergeClasses(styles.dropzone, isIntake && dragOver && styles.dropzoneActive)}
          data-testid={`dropzone-${column.id}`}
        >
          Drop cards here
        </div>
      ) : (
        <div className={styles.cards}>
          {column.cards.map((card, index) =>
            card.kind === 'task' ? (
              <div
                key={(card as TaskCardDto).task_id}
                onDragOver={isIntake ? (e) => { e.preventDefault(); } : undefined}
                onDrop={isIntake ? (e) => handleCardDrop(e, index) : undefined}
              >
                <TaskCard
                  card={card as TaskCardDto}
                  columnId={column.id}
                  projectId={projectId}
                  onMutated={onMutated}
                  onDragStartTask={onDragStartTask}
                  onDragEndTask={onDragEndTask}
                  isDragging={draggingTaskId === (card as TaskCardDto).task_id}
                />
              </div>
            ) : (
              <RunCard key={(card as RunCardDto).run_id} card={card as RunCardDto} projectId={projectId} onMutated={onMutated} />
            ),
          )}
        </div>
      )}

      {isTerminal && (column.collapsed_count ?? 0) > 0 && !includeTerminalHistory && (
        <Button appearance="subtle" size="small" onClick={onToggleTerminalHistory}>
          {`Show older (${column.collapsed_count})`}
        </Button>
      )}
      {isTerminal && includeTerminalHistory && (
        <Button appearance="subtle" size="small" onClick={onToggleTerminalHistory}>
          Show less
        </Button>
      )}
    </section>
  );
}
