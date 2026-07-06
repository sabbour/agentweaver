// Browser chat control console — command parser (Issue #50 / spec
// mcp-integrations/browser-chat-control-console).
//
// The console is a THIN CLIENT (constitution Principle III: the API is the
// single source of truth). This module contains NO business logic and NO
// network calls — it only turns a line of natural-language-ish input into a
// structured intent. The console component then drives that intent through the
// EXISTING authorized apiClient methods, exactly like the rest of the web app.
//
// Parsing is deliberately deterministic (keyword/verb based) rather than
// LLM-backed: there is no conversational backend endpoint in the API surface,
// and constitution VII forbids mocks/stubs. An honest keyword REPL that maps to
// real endpoints is preferable to faking an NL model.

export type ConsoleIntent =
  | { kind: 'help' }
  | { kind: 'list_projects' }
  | { kind: 'use_project'; query: string }
  | { kind: 'list_backlog' }
  | { kind: 'create_backlog'; title: string; description?: string }
  | { kind: 'promote_backlog'; query: string }
  | { kind: 'list_runs' }
  | { kind: 'start_orchestration'; goal: string }
  | { kind: 'monitor'; runId: string }
  // A request the parser understood the *shape* of, but which is missing a
  // required argument or is otherwise ambiguous. The console must ask for
  // clarification and MUST NOT create or start any work (spec edge case:
  // "asks for clarification instead of guessing and launching work").
  | { kind: 'clarify'; message: string }
  | { kind: 'unknown'; input: string };

export interface CommandHelp {
  usage: string;
  summary: string;
  status: 'available' | 'deferred';
}

// Canonical command reference — exported so both the UI and tests stay in sync.
export const CONSOLE_COMMANDS: CommandHelp[] = [
  { usage: 'help', summary: 'Show the list of console commands.', status: 'available' },
  { usage: 'projects', summary: 'List projects with links to each project.', status: 'available' },
  { usage: 'use <project>', summary: 'Select the active project for later commands.', status: 'available' },
  { usage: 'backlog', summary: "List the active project's backlog / ready items.", status: 'available' },
  { usage: 'add backlog <title> [:: <description>]', summary: 'Capture a new backlog item.', status: 'available' },
  { usage: 'ready <task title or id>', summary: 'Promote a backlog item to Ready.', status: 'available' },
  { usage: 'runs', summary: 'List orchestration/coordinator runs in the active project.', status: 'available' },
  { usage: 'orchestrate <goal>', summary: 'Start a coordinator orchestration (you confirm the Outcome plan on the run page).', status: 'available' },
  { usage: 'monitor <runId>', summary: 'Stream live updates for a run while preserving its event history.', status: 'available' },
];

// Commands intentionally NOT implemented in this slice. Surfaced in help so the
// boundary is explicit and the console never silently pretends to do them.
export const DEFERRED_COMMANDS: CommandHelp[] = [
  { usage: 'create project <name>', summary: 'Full project creation — use the Projects gallery wizard (needs repo/working dir/blueprint).', status: 'deferred' },
  { usage: 'edit / rank / decompose backlog', summary: 'Detailed backlog editing & work-breakdown — use the Board.', status: 'deferred' },
  { usage: 'approve / review / merge', summary: 'Confirmation, approval, review and merge gates stay in their existing gated views — the console links out, never bypasses them.', status: 'deferred' },
];

function stripLeading(input: string, ...prefixes: string[]): string | null {
  const lower = input.toLowerCase();
  for (const p of prefixes) {
    if (lower === p) return '';
    if (lower.startsWith(p + ' ')) return input.slice(p.length + 1).trim();
  }
  return null;
}

function matches(input: string, ...exact: string[]): boolean {
  return exact.includes(input.toLowerCase());
}

/**
 * Parse a single console line into a structured intent. Pure function.
 * Order matters: more specific verbs are checked before generic ones so that
 * e.g. "runs" is not swallowed by the "run <goal>" orchestration alias.
 */
export function parseConsoleCommand(raw: string): ConsoleIntent {
  const input = raw.trim();
  if (!input) return { kind: 'unknown', input: raw };

  if (matches(input, 'help', '?', 'commands', 'h')) return { kind: 'help' };

  if (matches(input, 'projects', 'list projects', 'show projects', 'ls projects')) {
    return { kind: 'list_projects' };
  }

  if (matches(input, 'backlog', 'list backlog', 'show backlog', 'ls backlog')) {
    return { kind: 'list_backlog' };
  }

  if (matches(input, 'runs', 'list runs', 'show runs', 'ls runs', 'orchestrations', 'list orchestrations')) {
    return { kind: 'list_runs' };
  }

  // use / select project
  {
    const q = stripLeading(input, 'use', 'select project', 'switch to', 'switch project', 'open project');
    if (q !== null) {
      if (!q) return { kind: 'clarify', message: 'Which project? Try `projects` to list them, then `use <name or id>`.' };
      return { kind: 'use_project', query: q };
    }
  }

  // create / capture backlog item
  {
    const q = stripLeading(
      input,
      'add backlog',
      'create backlog',
      'new backlog item',
      'new backlog',
      'add task',
      'capture task',
      'capture',
    );
    if (q !== null) {
      if (!q) return { kind: 'clarify', message: 'What should the backlog item say? Try `add backlog <title> :: <optional description>`.' };
      const [titlePart, ...descParts] = q.split('::');
      const title = titlePart.trim();
      const description = descParts.join('::').trim();
      if (!title) return { kind: 'clarify', message: 'The backlog item needs a title. Try `add backlog <title>`.' };
      return description
        ? { kind: 'create_backlog', title, description }
        : { kind: 'create_backlog', title };
    }
  }

  // promote to ready
  {
    const q = stripLeading(input, 'ready', 'promote', 'send to ready', 'move to ready');
    if (q !== null) {
      const cleaned = q.replace(/^to ready\s+/i, '').replace(/\s+to ready$/i, '').trim();
      if (!cleaned) return { kind: 'clarify', message: 'Which backlog item should move to Ready? Try `ready <task title or id>` (see `backlog`).' };
      return { kind: 'promote_backlog', query: cleaned };
    }
  }

  // monitor / watch a run
  {
    const q = stripLeading(input, 'monitor', 'watch', 'stream', 'tail');
    if (q !== null) {
      if (!q) return { kind: 'clarify', message: 'Which run should I monitor? Try `monitor <runId>` (see `runs`).' };
      return { kind: 'monitor', runId: q.split(/\s+/)[0] };
    }
  }

  // start orchestration (checked last: "start"/"run" are generic verbs)
  {
    const q = stripLeading(input, 'orchestrate', 'start orchestration', 'start', 'run', 'kick off', 'begin');
    if (q !== null) {
      if (!q) return { kind: 'clarify', message: 'What goal should the orchestration pursue? Try `orchestrate <goal>`.' };
      return { kind: 'start_orchestration', goal: q };
    }
  }

  return { kind: 'unknown', input };
}
