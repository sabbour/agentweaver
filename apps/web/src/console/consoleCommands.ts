// Browser control-console — slash-command catalog + tokenizer (Issue #50 / spec
// mcp-integrations/browser-chat-control-console).
//
// TWO CHANNELS (rubber-duck finding #9):
//   1. Free-form prose  → the conversational coordinator loop. Prose is sent to
//      the REAL coordinator agent via apiClient.steerCoordinator (constitution
//      VII — there is NO browser-side LLM / tool-router; the backend operator
//      agent that routes arbitrary NL to the whole MCP catalog is filed as #201).
//   2. /commands        → the explicit MCP-tool control plane. Each command wraps
//      the SAME authorized apiClient method / API endpoint that the corresponding
//      MCP tool wraps.
//
// SINGLE SOURCE OF TRUTH (finding #2 — drift risk): the command list below is the
// ONE typed table the UI, /help and tests all read. Each entry names the MCP tool
// family it mirrors. Keep these reconciled with the authoritative catalog at
// docs/reference/mcp-tools.md — do NOT hand-maintain a second divergent mapping.

export type SlashCommandName =
  | 'help'
  | 'projects'
  | 'use'
  | 'backlog'
  | 'add'
  | 'ready'
  | 'runs'
  | 'orchestrate'
  | 'monitor'
  | 'confirm'
  | 'revise'
  | 'approve-assembly'
  | 'stop'
  | 'clear';

export interface SlashCommandSpec {
  name: SlashCommandName;
  aliases: string[];
  /** Argument hint shown in /help, e.g. "<goal>". Empty when the command takes none. */
  argHint: string;
  summary: string;
  /**
   * The MCP tool family this control mirrors (see docs/reference/mcp-tools.md).
   * The console calls the same apiClient method the MCP tool wraps — this is a
   * thin client, not a parallel implementation (constitution III).
   */
  mcp: string;
}

// The one typed catalog. Order = /help display order.
export const SLASH_COMMANDS: SlashCommandSpec[] = [
  { name: 'help',     aliases: ['?', 'h'],            argHint: '',                    summary: 'Show this command reference.',                                       mcp: '—' },
  { name: 'projects', aliases: ['ls'],                argHint: '',                    summary: 'List projects with links.',                                          mcp: 'Project.list_projects' },
  { name: 'use',      aliases: ['select', 'project'], argHint: '<name or id>',        summary: 'Select the active project for later commands.',                      mcp: 'Project.get_project' },
  { name: 'backlog',  aliases: [],                    argHint: '',                    summary: "List the active project's backlog / ready items.",                   mcp: 'Backlog.get_board' },
  { name: 'add',      aliases: ['capture'],           argHint: '<title> [:: <desc>]', summary: 'Capture a new backlog item.',                                        mcp: 'Backlog.capture_backlog_task' },
  { name: 'ready',    aliases: ['promote'],           argHint: '<task title or id>',  summary: 'Promote a backlog item to Ready (picked up by the normal flow).',     mcp: 'Backlog.move_task_to_ready' },
  { name: 'runs',     aliases: ['orchestrations'],    argHint: '',                    summary: 'List coordinator/orchestration runs in the active project.',          mcp: 'Run.list_project_runs' },
  { name: 'orchestrate', aliases: ['start'],          argHint: '<goal>',              summary: 'Start a coordinator orchestration (Outcome plan is confirmed here).', mcp: 'Coordinator.start_orchestration' },
  { name: 'monitor',  aliases: ['watch', 'bind'],     argHint: '<runId>',             summary: 'Bind the terminal to a run: live stream + inline gates + history.',   mcp: 'Run.get_run_events' },
  { name: 'confirm',  aliases: [],                    argHint: '',                    summary: "Confirm the bound run's drafted Outcome plan gate.",                 mcp: 'Coordinator.confirm_outcome_spec' },
  { name: 'revise',   aliases: [],                    argHint: '<feedback>',          summary: "Revise the bound run's Outcome plan before confirming.",             mcp: 'Coordinator.revise_outcome_spec' },
  { name: 'approve-assembly', aliases: ['approve'],   argHint: '[comment]',           summary: "Approve the bound run's collective assembly review gate.",           mcp: 'Coordinator.review_assembly' },
  { name: 'stop',     aliases: [],                    argHint: '',                    summary: 'Stop the bound coordinator orchestration.',                          mcp: 'Coordinator.steer_coordinator (stop)' },
  { name: 'clear',    aliases: ['cls'],               argHint: '',                    summary: 'Clear the local transcript (does not affect the run).',              mcp: '—' },
];

// Deferred capabilities — intentionally NOT wired in this slice. Listed in /help so the
// console never silently pretends to perform them; the linked gated UIs own these actions.
export const DEFERRED_COMMANDS: Array<{ label: string; summary: string }> = [
  { label: '/new-project',   summary: 'Full project creation (repo/working dir/blueprint) — use the Projects gallery wizard.' },
  { label: '/decompose',     summary: 'Detailed work-breakdown / backlog editing — use the Board.' },
  { label: '/review /merge', summary: 'Worker review & merge gates stay in their gated run views — the console links to the appropriate surface.' },
];

const NAME_BY_TOKEN = new Map<string, SlashCommandName>();
for (const c of SLASH_COMMANDS) {
  NAME_BY_TOKEN.set(c.name, c.name);
  for (const a of c.aliases) NAME_BY_TOKEN.set(a, c.name);
}

export type ParsedInput =
  // Explicit MCP-tool control-plane command.
  | { channel: 'command'; name: SlashCommandName; arg: string; raw: string }
  // A slash token that matched no known command.
  | { channel: 'unknown-command'; token: string; raw: string }
  // Free-form prose → conversational coordinator loop.
  | { channel: 'prose'; text: string; raw: string };

/**
 * Tokenize a single input line into a channel. Pure function, no I/O.
 * A leading '/' selects the explicit command channel; everything else is prose
 * routed to the coordinator agent.
 */
export function parseInput(raw: string): ParsedInput {
  const trimmed = raw.trim();
  if (!trimmed.startsWith('/')) {
    return { channel: 'prose', text: trimmed, raw };
  }
  const withoutSlash = trimmed.slice(1);
  const spaceIdx = withoutSlash.search(/\s/);
  const token = (spaceIdx === -1 ? withoutSlash : withoutSlash.slice(0, spaceIdx)).toLowerCase();
  const arg = spaceIdx === -1 ? '' : withoutSlash.slice(spaceIdx + 1).trim();
  const name = NAME_BY_TOKEN.get(token);
  if (!name) return { channel: 'unknown-command', token, raw };
  return { channel: 'command', name, arg, raw };
}
