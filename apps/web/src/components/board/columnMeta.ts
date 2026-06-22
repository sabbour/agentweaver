import { tokens } from '@fluentui/react-components';

// Deterministic accent palette + stage copy for the Kanban board columns. Kept in a
// standalone module (not the component file) so React Fast Refresh stays happy and so
// the mapping is unit-testable in isolation.

// Accent palette for the dynamic workflow-stage columns. Backlog and Ready are fixed
// (gray / blue); every other stage cycles this list keyed by its position among the
// workflow columns, so colors stay stable across renders.
export const COLUMN_ACCENT_CYCLE: string[] = [
  tokens.colorPaletteMarigoldBorderActive, // orange  (e.g. In Progress)
  tokens.colorPalettePurpleBorderActive,   // purple  (e.g. In Review)
  tokens.colorPaletteTealBorderActive,     // teal
  tokens.colorPaletteGreenBorderActive,    // green
  tokens.colorPaletteBerryBorderActive,    // berry
  tokens.colorPalettePeachBorderActive,    // peach
];

// Resolve a column's left-accent color. `workflowIndex` is the column's 0-based
// position among the non-intake (workflow) columns; it is ignored for backlog/ready.
export function columnAccentColor(columnId: string, workflowIndex: number): string {
  if (columnId === 'backlog') return tokens.colorNeutralStroke1;
  if (columnId === 'ready') return tokens.colorPaletteBlueBorderActive;
  return COLUMN_ACCENT_CYCLE[workflowIndex % COLUMN_ACCENT_CYCLE.length];
}

// Human copy for the known intake stages. Dynamic workflow stages have no entry and
// degrade gracefully (no subtitle) rather than printing "undefined".
export const STAGE_DESCRIPTIONS: Record<string, string> = {
  backlog: "Captured but not yet committed to. Things you're considering.",
  ready: 'Committed work that the coordinator and Ralph monitor may pick up next.',
};
