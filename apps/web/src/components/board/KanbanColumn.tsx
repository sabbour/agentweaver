import { Badge, Button, Caption1, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useState } from 'react';
import type { BoardColumnDto, RunCardDto, TaskCardDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';
import { TaskCard } from './TaskCard';
import { RunCard } from './RunCard';

const useStyles = makeStyles({
  column: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    flex: '0 0 280px',
    width: '280px',
    maxHeight: '100%',
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  dropActive: {
    border: `1px dashed ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minHeight: '40px',
    overflowY: 'auto',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
    padding: tokens.spacingVerticalXS,
  },
});

export interface KanbanColumnProps {
  column: BoardColumnDto;
  projectId: string;
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
    column, projectId, onMutated, onDropTask, onRejectDrop,
    onDragStartTask, onDragEndTask, draggingTaskId,
    includeTerminalHistory, onToggleTerminalHistory,
  } = props;

  const [sendingAll, setSendingAll] = useState(false);

  const isIntake = column.kind === 'intake';
  const isTerminal = column.kind === 'workflow' && (column.id === 'terminal' || column.collapsed_count != null);

  const handleSendAllToReady = async () => {
    setSendingAll(true);
    try {
      await apiClient.sendAllBacklogToReady(projectId);
      await onMutated();
    } finally {
      setSendingAll(false);
    }
  };

  // Intake columns are drop targets. Workflow columns are NOT — dropping there is
  // rejected (no API call) and surfaces a MessageBar via onRejectDrop.
  const intakeHandlers = isIntake
    ? {
        onDragOver: (e: React.DragEvent) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; },
        onDrop: (e: React.DragEvent) => {
          e.preventDefault();
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
      aria-label={`${column.label} column`}
      data-testid={`column-${column.id}`}
      data-column-kind={column.kind}
      {...intakeHandlers}
    >
      <div className={styles.header}>
        <Text className={styles.label}>{column.label}</Text>
        <Badge appearance="tint" color="informative">{column.cards.length}</Badge>
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
      </div>

      <div className={styles.cards}>
        {column.cards.length === 0 && <Caption1 className={styles.empty}>No items</Caption1>}
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
            <RunCard key={(card as RunCardDto).run_id} card={card as RunCardDto} projectId={projectId} />
          ),
        )}
      </div>

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
