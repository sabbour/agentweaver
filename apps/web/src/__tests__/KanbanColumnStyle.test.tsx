import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, within, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme, tokens } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';
import { KanbanColumn } from '../components/board/KanbanColumn';
import { columnAccentColor, STAGE_DESCRIPTIONS } from '../components/board/columnMeta';
import type { BoardColumnDto, TaskCardDto } from '../api/types';

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
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
  it('renders the left accent color and description for a known stage (Ready = blue)', () => {
    const column: BoardColumnDto = {
      id: 'ready', kind: 'intake', label: 'Ready',
      cards: [task('t1', 'A ready task')],
    };
    renderColumn(column, columnAccentColor('ready', 0));

    const section = screen.getByTestId('column-ready');
    // Blue accent for Ready (fixed mapping).
    expect(section.getAttribute('data-accent-color')).toBe(tokens.colorPaletteBlueBorderActive);
    expect((section as HTMLElement).style.borderLeftColor).toBe(tokens.colorPaletteBlueBorderActive);
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

  it('renders a "Drop cards here" dropzone for an empty column (no plain "No items")', () => {
    const column: BoardColumnDto = { id: 'ready', kind: 'intake', label: 'Ready', cards: [] };
    renderColumn(column, columnAccentColor('ready', 0));

    expect(screen.getByTestId('dropzone-ready')).toBeTruthy();
    expect(screen.getByText('Drop cards here')).toBeTruthy();
    expect(screen.queryByText('No items')).toBeNull();
  });

  it('omits the subtitle for a dynamic workflow stage without known copy (no "undefined")', () => {
    const column: BoardColumnDto = { id: 'planned:assembly-custom', kind: 'workflow', label: 'Custom Stage', cards: [] };
    renderColumn(column, columnAccentColor('planned:assembly-custom', 0));

    const section = screen.getByTestId('column-planned:assembly-custom');
    expect(within(section).queryByText('undefined')).toBeNull();
    // Dynamic workflow stages cycle the palette (first one = marigold/orange).
    expect(section.getAttribute('data-accent-color')).toBe(columnAccentColor('planned:assembly-custom', 0));
  });
});

describe('columnAccentColor — palette mapping', () => {
  it('maps backlog -> gray, ready -> blue, and cycles the workflow palette', () => {
    expect(columnAccentColor('backlog', 0)).toBe(tokens.colorNeutralStroke1);
    expect(columnAccentColor('ready', 0)).toBe(tokens.colorPaletteBlueBorderActive);
    expect(columnAccentColor('coordinator', 0)).toBe(tokens.colorPaletteMarigoldBorderActive);
    expect(columnAccentColor('in-review', 1)).toBe(tokens.colorPalettePurpleBorderActive);
  });
});
