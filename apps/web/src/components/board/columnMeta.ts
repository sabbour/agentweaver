import { tokens } from '@fluentui/react-components';
import type { BoardCardDto, BoardColumnDto, RunCardDto } from '../../api/types';

// Deterministic accent palette + stage copy for the Kanban board columns. Kept in a
// standalone module (not the component file) so React Fast Refresh stays happy and so
// the mapping is unit-testable in isolation.

export const FIXED_BOARD_COLUMNS: ReadonlyArray<Pick<BoardColumnDto, 'id' | 'kind' | 'label'>> = [
  { id: 'backlog', kind: 'intake', label: 'Backlog' },
  { id: 'ready', kind: 'intake', label: 'Ready' },
  { id: 'problems', kind: 'workflow', label: 'Problems' },
  { id: 'human-review', kind: 'workflow', label: 'Human Review' },
  { id: 'active', kind: 'workflow', label: 'Active' },
  { id: 'done', kind: 'workflow', label: 'Done' },
];

// Resolve a column's left-accent color. `workflowIndex` is the column's 0-based
// position among the non-intake (workflow) columns; it is ignored for backlog/ready.
export function columnAccentColor(columnId: string, _workflowIndex: number): string {
  if (columnId === 'backlog') return tokens.colorNeutralStroke1;
  if (columnId === 'ready') return tokens.colorPaletteBlueBorderActive;
  if (columnId === 'problems') return tokens.colorPaletteRedBorderActive;
  if (columnId === 'human-review') return tokens.colorPalettePurpleBorderActive;
  if (columnId === 'active') return tokens.colorPaletteTealBorderActive;
  if (columnId === 'done') return tokens.colorPaletteGreenBorderActive;
  return tokens.colorNeutralStroke1;
}

// Human copy for the known intake stages. Dynamic workflow stages have no entry and
// degrade gracefully (no subtitle) rather than printing "undefined".
export const STAGE_DESCRIPTIONS: Record<string, string> = {
  backlog: "Captured but not yet committed to. Things you're considering.",
  ready: 'Committed work that the coordinator and Ralph monitor may pick up next.',
  problems: 'Blocked, failed, declined, or otherwise needs attention.',
  'human-review': 'Work waiting for a person to review or approve.',
  active: 'Work currently moving through the coordinator workflow.',
  done: 'Completed or merged work.',
};

function normalized(value: string | null | undefined): string {
  return (value ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function isArchivedCard(card: BoardCardDto): boolean {
  if (card.archived_at) return true;
  if (card.kind === 'task') return normalized(card.state) === 'archived';
  return normalized(card.status) === 'archived';
}

function runText(card: RunCardDto, source: BoardColumnDto): string {
  return [
    card.status,
    card.work_plan_status,
    card.assembly_stage,
    card.stage_id,
    source.id,
    source.label,
  ].map(normalized).join(' ');
}

function fixedColumnForCard(card: BoardCardDto, source: BoardColumnDto): string | null {
  if (isArchivedCard(card) || normalized(source.id).includes('archive')) return null;

  if (card.kind === 'task') {
    const state = normalized(card.state);
    if (state === 'backlog') return 'backlog';
    if (state === 'ready') return 'ready';
    return source.id === 'backlog' || source.id === 'ready' ? source.id : null;
  }

  const text = runText(card, source);
  if (/(fail|declin|block|problem|error)/.test(text)) return 'problems';
  if (/(humanreview|awaitingreview|inreview|review)/.test(text)) return 'human-review';
  if (/(terminal|done|complete|completed|merged)/.test(text)) return 'done';
  return 'active';
}

export function fixedBoardColumns(columns: BoardColumnDto[]): BoardColumnDto[] {
  const byId = new Map<string, BoardColumnDto>(
    FIXED_BOARD_COLUMNS.map((col) => [col.id, { ...col, cards: [] }]),
  );

  for (const source of columns) {
    for (const card of source.cards) {
      const targetId = fixedColumnForCard(card, source);
      if (!targetId) continue;
      byId.get(targetId)?.cards.push(card);
    }

    if (source.collapsed_count && (source.id === 'terminal' || source.id === 'done')) {
      const done = byId.get('done');
      if (done) done.collapsed_count = (done.collapsed_count ?? 0) + source.collapsed_count;
    }
  }

  return FIXED_BOARD_COLUMNS.map((col) => byId.get(col.id) ?? { ...col, cards: [] });
}
