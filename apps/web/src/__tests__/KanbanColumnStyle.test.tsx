import {
  AzureFluentProvider } from '../copilot-fluent-system';
import { columnAccentColor,
  STAGE_DESCRIPTIONS } from '../components/board/columnMeta';
import { KanbanColumn } from '../components/board/KanbanColumn';
import { tokens,
} from '../copilot-fluent-system';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { BoardColumnDto, TaskCardDto } from '../api/types';
import type { ReactNode } from 'react';
function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>{children}</MemoryRouter>
    </AzureFluentProvider>
  );
}

function task(id: string, title: string): TaskCardDto {
  return { kind: 'task', task_id: id, title, description: null, state: 'backlog', order_key: 'a', captured_by: 'alice', created_at: '2026-01-01T00:00:00Z' };
}

function renderColumn(column: BoardColumnDto, accentColor: string) {
  return render(
    <Wrapper>
      <KanbanColumn
        column={column}
        accentColor={accentColor}
        projectId="proj-1"
        onMutated={vi.fn()}
        onDropTask={vi.fn()}
        onRejectDrop={vi.fn()}
        onDragStartTask={vi.fn()}
        onDragEndTask={vi.fn()}
        draggingTaskId={null}
        includeTerminalHistory={false}
        onToggleTerminalHistory={vi.fn()}
      />
    </Wrapper>,
  );
}

beforeEach(() => { vi.clearAllMocks(); });
afterEach(() => { cleanup(); });

describe('KanbanColumn — Squadboard restyle', () => {
  it('renders the header accent color and description for a known stage (Ready = marigold)', () => {
    const column: BoardColumnDto = {
      id: 'ready', kind: 'intake', label: 'Ready',
      cards: [task('t1', 'A ready task')],
    };
    renderColumn(column, columnAccentColor('ready', 0));

    const section = screen.getByTestId('column-ready');
    // Marigold accent for Ready (intentional no-blue design system change).
    expect(section.getAttribute('data-accent-color')).toBe(tokens.colorPaletteMarigoldBorderActive);
    expect((section as HTMLElement).style.borderColor).not.toBe(tokens.colorPaletteMarigoldBorderActive);
    expect(within(section).getByLabelText('Ready summary')).toBeTruthy();
    // Real description copy, not "undefined".
    expect(within(section).getByText(STAGE_DESCRIPTIONS.ready)).toBeTruthy();
    expect(within(section).queryByText('undefined')).toBeNull();
  });

  it('uses the gray accent for Backlog and renders its description', () => {
    const column: BoardColumnDto = {
      id: 'backlog', kind: 'intake', label: 'Backlog',
      cards: [task('t1', 'A backlog task')],
    };
    renderColumn(column, columnAccentColor('backlog', 0));

    const section = screen.getByTestId('column-backlog');
    expect(section.getAttribute('data-accent-color')).toBe(tokens.colorNeutralStroke1);
    expect(within(section).getByText(STAGE_DESCRIPTIONS.backlog)).toBeTruthy();
  });

  it('renders the count chip with the card count', () => {
    const column: BoardColumnDto = {
      id: 'backlog', kind: 'intake', label: 'Backlog',
      cards: [task('t1', 'one'), task('t2', 'two'), task('t3', 'three')],
    };
    renderColumn(column, columnAccentColor('backlog', 0));

    expect(screen.getByTestId('count-backlog').textContent).toBe('3');
  });

  it('renders orchestration dropzone copy for an empty intake column (no plain "No items")', () => {
    const column: BoardColumnDto = { id: 'ready', kind: 'intake', label: 'Ready', cards: [] };
    renderColumn(column, columnAccentColor('ready', 0));

    expect(screen.getByTestId('dropzone-ready')).toBeTruthy();
    expect(screen.getByText('Drop tasks here to queue them.')).toBeTruthy();
    expect(screen.queryByText('No items')).toBeNull();
  });

  it('renders canonical workflow bucket descriptions without "undefined"', () => {
    const column: BoardColumnDto = { id: 'human-review', kind: 'workflow', label: 'Human Review', cards: [] };
    renderColumn(column, columnAccentColor('human-review', 0));

    const section = screen.getByTestId('column-human-review');
    expect(within(section).getByText(STAGE_DESCRIPTIONS['human-review'])).toBeTruthy();
    expect(within(section).queryByText('undefined')).toBeNull();
  });
});

describe('columnAccentColor — palette mapping', () => {
  it('maps the fixed board columns to stable accents', () => {
    expect(columnAccentColor('backlog', 0)).toBe(tokens.colorNeutralStroke1);
    expect(columnAccentColor('ready', 0)).toBe(tokens.colorPaletteMarigoldBorderActive);
    expect(columnAccentColor('problems', 0)).toBe(tokens.colorPaletteRedBorderActive);
    expect(columnAccentColor('human-review', 1)).toBe(tokens.colorPalettePurpleBorderActive);
    expect(columnAccentColor('active', 2)).toBe(tokens.colorPaletteTealBorderActive);
    expect(columnAccentColor('done', 3)).toBe(tokens.colorPaletteGreenBorderActive);
  });
});
