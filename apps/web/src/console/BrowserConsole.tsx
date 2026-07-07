import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import {
  Badge,
  Button,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  shorthands,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwise16Regular,
  CheckmarkCircle16Regular,
  Open16Regular,
  Send16Regular,
  Warning16Regular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { BoardColumnDto, Project, TaskCardDto } from '../api/types';
import { Timeline } from '../components/Timeline';
import { useCoordinatorRunModel } from '../hooks/useCoordinatorRunModel';
import {
  DEFERRED_COMMANDS,
  SLASH_COMMANDS,
  parseInput,
  type SlashCommandName,
} from './consoleCommands';

// Browser control-console TUI (Issue #50). A terminal-styled, chat-based
// ALTERNATIVE UX to operate Agentweaver end-to-end. It is a thin PRESENTATION
// over the SAME session/streaming/tool infrastructure the run pages use
// (constitution III): it reuses useCoordinatorRunModel → useSeededRunStream →
// useRunStream + useTimelineItems + <Timeline> (which renders AgentMessageBubble,
// tool calls, LifecycleEventCard, QuestionAnswerCard and every HITL gate). It
// NEVER executes agent work itself and NEVER bypasses a gate — prose goes to the
// REAL coordinator agent (steerCoordinator) and /commands wrap the same endpoints
// the MCP tools wrap (see consoleCommands.ts → docs/reference/mcp-tools.md).
//
// PLUGGABLE TURN SOURCES (finding #9): the bound-run panel is fed by ONE typed
// "turn source" — today the coordinator run stream (boundRunKind='coordinator').
// The backend operator-agent run stream (#201) drops in as another source without
// a rewrite: bind its runId + kind and the same <Timeline> renders it.

type TurnSourceKind = 'coordinator';

// Dedicated terminal color scope (constitution II allows a scoped palette for the
// console's TUI surface). These stay local to this shell and never leak into the
// shared Fluent-themed pages.
const TERM = {
  bg: '#0b0f14',
  bgRaised: '#0e141b',
  fg: '#d6dee6',
  fgMuted: '#7c8b9a',
  fgDim: '#5b6b7a',
  border: '#1e2833',
  accent: '#8fd0ff',
  prompt: '#6fd08f',
  branch: '#e6c07b',
  ok: '#8fe3a6',
  warn: '#ffcf6b',
  err: '#ff8a80',
  mono: 'Consolas, "Cascadia Code", "SF Mono", Menlo, monospace',
} as const;

const useStyles = makeStyles({
  // Full-height terminal surface: header (compact) → scrollback (flex, scrolls)
  // → prompt line (pinned bottom), like a real CLI.
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    backgroundColor: TERM.bg,
    color: TERM.fg,
    fontFamily: TERM.mono,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '1.5',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    rowGap: '6px',
    flexShrink: 0,
    // Generous gap between the command cluster and the status cluster — they
    // are two distinct groups, not a row of equal peers.
    gap: tokens.spacingHorizontalL,
    ...shorthands.borderBottom('1px', 'solid', TERM.border),
    ...shorthands.padding('6px', tokens.spacingHorizontalM),
    backgroundColor: TERM.bgRaised,
  },
  // Title + quick-command chips read as ONE command cluster: tight internal
  // gap so they group visually, separated from the status cluster by the
  // header's own gap rather than by matching spacing throughout.
  headerCommandGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    rowGap: '4px',
    minWidth: 0,
  },
  title: { fontWeight: 600, letterSpacing: '0.04em', color: TERM.accent, flexShrink: 0 },
  // Quick command affordances live next to the title — plain bracketed text
  // chips, not SaaS-style buttons/cards, so the status rail stays terminal-native.
  headerChips: { display: 'flex', alignItems: 'center', gap: '2px', flexWrap: 'wrap' },
  chip: {
    fontFamily: TERM.mono,
    fontSize: tokens.fontSizeBase200,
    color: TERM.fgMuted,
    backgroundColor: 'transparent',
    minHeight: '24px',
    ...shorthands.padding('2px', '8px'),
    ...shorthands.border('1px', 'solid', TERM.border),
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
    ':hover': { color: TERM.fg, ...shorthands.borderColor(TERM.accent) },
    ':focus-visible': { outlineColor: TERM.accent, outlineStyle: 'solid', outlineWidth: '2px', outlineOffset: '1px' },
    // Compensate hit-area for touch pointers without growing the visual chip.
    '@media (pointer: coarse)': { minHeight: '44px', minWidth: '44px' },
  },
  // Project / run / busy-state read as ONE status cluster, right-aligned
  // against the command cluster — a familiar two-zone terminal status bar.
  headerMeta: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  headerLabel: { color: TERM.fgMuted },
  busyIndicator: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: TERM.warn },
  // The scrollback fills all remaining height — no more cramped 42% void.
  body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' },
  transcript: {
    flex: '1 1 auto',
    minHeight: 0,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: '1px',
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalM),
  },
  // Applied ONLY when a run panel is actually bound: on narrow/tablet widths
  // the run panel and the pinned prompt both need room, so the transcript
  // yields height instead of pushing them off-screen. With no run bound,
  // the transcript is the only content below the header and should use all
  // available vertical space rather than capping itself pre-emptively.
  transcriptCapped: {
    '@media (max-width: 680px)': { flex: '0 1 30vh', maxHeight: '30vh' },
  },
  line: { whiteSpace: 'pre-wrap', wordBreak: 'break-word', display: 'flex', gap: tokens.spacingHorizontalXS },
  // Each new user-submitted command starts a fresh "block" — a small top gap so a
  // scrollback of many exchanges stays scannable without borders/nested cards.
  blockStart: { marginTop: tokens.spacingVerticalM },
  gutter: { flexShrink: 0, width: '1.1em', textAlign: 'center', userSelect: 'none' },
  promptGlyph: { color: TERM.prompt },
  sysGlyph: { color: TERM.fgDim },
  errText: { color: TERM.err },
  warnText: { color: TERM.warn },
  okText: { color: TERM.ok },
  linkList: { display: 'flex', flexDirection: 'column', gap: '2px', marginLeft: '1.5em', marginTop: '2px' },
  link: {
    color: TERM.accent,
    textDecorationLine: 'none',
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    minHeight: '20px',
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
    ':hover': { textDecorationLine: 'underline' },
    ':focus-visible': { outlineColor: TERM.accent, outlineStyle: 'solid', outlineWidth: '2px', outlineOffset: '2px' },
    '@media (pointer: coarse)': { minHeight: '44px' },
  },
  // Bound-run panel shares the scrollback space (flex-grows past the console log).
  runPanel: {
    flex: '2 1 0',
    minHeight: '160px',
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.borderTop('1px', 'solid', TERM.border),
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    '@media (max-width: 680px)': { flex: '1 1 auto', minHeight: '220px' },
  },
  runPanelHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    rowGap: '4px',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalXS, tokens.spacingHorizontalM),
    backgroundColor: tokens.colorNeutralBackground3,
    ...shorthands.borderBottom('1px', 'solid', tokens.colorNeutralStroke2),
  },
  // Status badges + reconnect + full-run link form one action group that
  // wraps as a unit on narrow widths instead of overflowing the header.
  runPanelActions: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    rowGap: '4px',
    gap: tokens.spacingHorizontalS,
  },
  gateBar: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    ...shorthands.padding(tokens.spacingVerticalXS, tokens.spacingHorizontalM),
    backgroundColor: tokens.colorNeutralBackground2,
    ...shorthands.borderBottom('1px', 'solid', tokens.colorNeutralStroke2),
  },
  // Scoped terminal look for the shared <Timeline> — monospace + dense spacing
  // applied to DESCENDANTS only. The Timeline component itself is untouched.
  timelineScroll: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    ...shorthands.padding(tokens.spacingHorizontalM),
    '& *': { fontFamily: TERM.mono },
  },
  // CLI-style prompt line pinned to the bottom of the terminal.
  promptLine: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalXS,
    ...shorthands.borderTop('1px', 'solid', TERM.border),
    ...shorthands.padding('6px', tokens.spacingHorizontalM),
    backgroundColor: TERM.bgRaised,
  },
  promptContext: {
    flexShrink: 0,
    userSelect: 'none',
    paddingTop: '5px',
    whiteSpace: 'nowrap',
    // Below ~560px the branch/path context crowds out the input; drop it and
    // keep just the caret so typing stays comfortable on phones.
    '@media (max-width: 560px)': {
      '& > *:not(:last-child)': { display: 'none' },
    },
  },
  promptPath: { color: TERM.accent },
  promptBranch: { color: TERM.branch },
  promptCaret: { color: TERM.prompt, marginLeft: '0.4em', marginRight: '0.2em' },
  // Discoverable submit shortcut — state feedback only, no onboarding overlay.
  promptHint: {
    flexShrink: 0,
    color: TERM.fgDim,
    fontSize: tokens.fontSizeBase100,
    whiteSpace: 'nowrap',
    paddingTop: '6px',
    '@media (max-width: 640px)': { display: 'none' },
  },
  blink: {
    animationName: {
      '0%, 49%': { opacity: 1 },
      '50%, 100%': { opacity: 0 },
    },
    animationDuration: '1.05s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'step-end',
  },
  // Borderless, transparent input that reads as part of the prompt line.
  input: {
    flex: 1,
    minWidth: 0,
    backgroundColor: 'transparent',
    ...shorthands.border('0'),
    ...shorthands.padding('0'),
    '::after': { display: 'none' },
    '& textarea': {
      fontFamily: TERM.mono,
      fontSize: tokens.fontSizeBase200,
      lineHeight: '1.5',
      color: TERM.fg,
      backgroundColor: 'transparent',
      ...shorthands.padding('0'),
      '::placeholder': { color: TERM.fgDim },
    },
  },
  sendBtn: {
    flexShrink: 0,
    '@media (pointer: coarse)': { minHeight: '44px', minWidth: '44px' },
  },
});

interface ConsoleLink { label: string; to: string }
type LineTone = 'user' | 'info' | 'ok' | 'warn' | 'error';
interface ConsoleLine {
  id: string;
  tone: LineTone;
  text: string;
  links?: ConsoleLink[];
}

interface CommandResult {
  tone: Exclude<LineTone, 'user'>;
  text: string;
  links?: ConsoleLink[];
  setActiveProject?: Project | null;
  bindRunId?: string;
  bindRunStatus?: string;
}

let seq = 0;
const nextId = () => `l${Date.now()}-${seq++}`;

function errText(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body}`;
  return err instanceof Error ? err.message : String(err);
}

function resolveProject(projects: Project[], query: string): { project?: Project; candidates?: Project[] } {
  const q = query.trim().toLowerCase();
  const byId = projects.find((p) => p.project_id.toLowerCase() === q);
  if (byId) return { project: byId };
  const byName = projects.filter((p) => p.name.toLowerCase() === q);
  if (byName.length === 1) return { project: byName[0] };
  const bySub = projects.filter((p) => p.name.toLowerCase().includes(q) || p.project_id.toLowerCase().includes(q));
  if (bySub.length === 1) return { project: bySub[0] };
  if (bySub.length > 1) return { candidates: bySub };
  return {};
}

function intakeCards(columns: BoardColumnDto[]): TaskCardDto[] {
  return columns
    .filter((c) => c.kind === 'intake')
    .flatMap((c) => c.cards)
    .filter((card): card is TaskCardDto => card.kind === 'task');
}

function buildHelp(): string {
  // Align the command column so summaries read as a scannable table, not a
  // ragged list — dense and predictable, like `man` or `--help` output.
  const rows = SLASH_COMMANDS.map((c) => ({
    head: `/${c.name}${c.argHint ? ' ' + c.argHint : ''}`,
    summary: c.summary,
    mcp: c.mcp,
  }));
  const col = Math.min(30, Math.max(...rows.map((r) => r.head.length)) + 2);
  const cmds = rows
    .map((r) => `  ${r.head.padEnd(col)}${r.summary}\n${' '.repeat(col + 2)}[MCP: ${r.mcp}]`)
    .join('\n');
  const deferred = DEFERRED_COMMANDS.map((c) => `  ${c.label} — ${c.summary}`).join('\n');
  return (
    'Two ways to drive Agentweaver:\n' +
    '  • Type PROSE (no leading slash) to talk to the coordinator agent — it plans,\n' +
    '    dispatches and assembles work. Bind a run first with /orchestrate or /monitor.\n' +
    '  • Type /commands for the explicit control plane. Each wraps the SAME endpoint\n' +
    '    the matching MCP tool wraps (docs/reference/mcp-tools.md).\n\n' +
    `Commands:\n${cmds}\n\nDeferred (use the linked gated UIs — the console never bypasses these):\n${deferred}`
  );
}

const GREETING: ConsoleLine = {
  id: 'greeting',
  tone: 'info',
  text:
    'agentweaver control console — terminal UX. Prose drives the real coordinator agent; ' +
    '/commands drive the MCP-backed control plane.\n\n' +
    'Quick start:\n' +
    '  /projects              list projects\n' +
    '  /use <name or id>      set the active project\n' +
    '  /orchestrate <goal>    start a coordinator run (confirms a plan before dispatch)\n' +
    '  /monitor <runId>       bind the terminal to an existing run\n' +
    '  /help                  full command + gate reference',
};

export function BrowserConsole() {
  const styles = useStyles();
  const [lines, setLines] = useState<ConsoleLine[]>([GREETING]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [activeProject, setActiveProject] = useState<Project | null>(null);
  const [boundRunId, setBoundRunId] = useState('');
  const [boundRunStatus, setBoundRunStatus] = useState<string | undefined>(undefined);
  const [boundRunKind] = useState<TurnSourceKind>('coordinator');
  // A prose goal captured while NO run is bound — held for EXPLICIT confirmation
  // rather than auto-starting work (spec: ambiguous requests ask before creating).
  const [pendingGoal, setPendingGoal] = useState<string | null>(null);

  const activeProjectRef = useRef<Project | null>(null);
  useEffect(() => { activeProjectRef.current = activeProject; }, [activeProject]);

  // The bound run's stream/timeline/gate model — inert while boundRunId is ''.
  const model = useCoordinatorRunModel(boundRunId, boundRunStatus);

  const transcriptRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = transcriptRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [lines]);

  const timelineScrollRef = useRef<HTMLDivElement>(null);
  const userScrolledUp = useRef(false);
  const onTimelineScroll = () => {
    const el = timelineScrollRef.current;
    if (!el) return;
    userScrolledUp.current = el.scrollHeight - el.scrollTop - el.clientHeight > 64;
  };
  useEffect(() => {
    if (!userScrolledUp.current && timelineScrollRef.current) {
      timelineScrollRef.current.scrollTop = timelineScrollRef.current.scrollHeight;
    }
  }, [model.items.length]);

  const appendLine = useCallback((tone: LineTone, text: string, links?: ConsoleLink[]) => {
    setLines((prev) => [...prev, { id: nextId(), tone, text, links }]);
  }, []);

  const projectLink = useCallback((p: Project): ConsoleLink => ({ label: `Open ${p.name}`, to: `/projects/${p.project_id}` }), []);

  const runCommand = useCallback(async (name: SlashCommandName, arg: string): Promise<CommandResult> => {
    const requireProject = (): Project | null => activeProjectRef.current;
    const requireBound = (): string | null => (boundRunId ? boundRunId : null);

    switch (name) {
      case 'help':
        return { tone: 'info', text: buildHelp() };
      case 'clear':
        setLines([GREETING]);
        return { tone: 'info', text: 'Transcript cleared.' };
      case 'projects': {
        const projects = await apiClient.listProjects();
        if (projects.length === 0) {
          return { tone: 'info', text: 'No projects yet.', links: [{ label: 'Open Projects gallery', to: '/projects' }] };
        }
        return {
          tone: 'info',
          text: `${projects.length} project(s). Select one with /use <name or id>:`,
          links: projects.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` })),
        };
      }
      case 'use': {
        if (!arg) return { tone: 'warn', text: 'Which project? Try /projects then /use <name or id>.' };
        const projects = await apiClient.listProjects();
        const { project, candidates } = resolveProject(projects, arg);
        if (project) {
          return { tone: 'ok', text: `Active project → ${project.name} (${project.project_id}).`, links: [projectLink(project)], setActiveProject: project };
        }
        if (candidates && candidates.length > 1) {
          return {
            tone: 'warn',
            text: `"${arg}" matches ${candidates.length} projects. Re-run /use with a specific id:`,
            links: candidates.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` })),
          };
        }
        return { tone: 'error', text: `No project matches "${arg}". Run /projects to list them.` };
      }
      case 'backlog': {
        const p = requireProject();
        if (!p) return { tone: 'warn', text: 'Select a project first with /use <project>.' };
        const board = await apiClient.getBoard(p.project_id);
        const cards = intakeCards(board.columns);
        if (cards.length === 0) return { tone: 'info', text: `No backlog/ready items in ${p.name}.`, links: [{ label: 'Open board', to: `/projects/${p.project_id}/board` }] };
        const body = cards.map((c) => `  [${c.state}] ${c.title} (${c.task_id})`).join('\n');
        return { tone: 'info', text: `${cards.length} intake item(s) in ${p.name}:\n${body}`, links: [{ label: 'Open board', to: `/projects/${p.project_id}/board` }] };
      }
      case 'add': {
        const p = requireProject();
        if (!p) return { tone: 'warn', text: 'Select a project first with /use <project>.' };
        if (!arg) return { tone: 'warn', text: 'What should the item say? Try /add <title> :: <optional description>.' };
        const [titlePart, ...rest] = arg.split('::');
        const title = titlePart.trim();
        const description = rest.join('::').trim() || null;
        if (!title) return { tone: 'warn', text: 'The backlog item needs a title. Try /add <title>.' };
        const task = await apiClient.captureBacklogTask(p.project_id, { title, description });
        return { tone: 'ok', text: `Captured "${task.title}" (${task.task_id}) in ${p.name}.`, links: [{ label: 'View on board', to: `/projects/${p.project_id}/board` }] };
      }
      case 'ready': {
        const p = requireProject();
        if (!p) return { tone: 'warn', text: 'Select a project first with /use <project>.' };
        if (!arg) return { tone: 'warn', text: 'Which backlog item? Try /ready <task title or id> (see /backlog).' };
        const board = await apiClient.getBoard(p.project_id);
        const backlog = intakeCards(board.columns).filter((c) => c.state === 'backlog');
        const q = arg.toLowerCase();
        const byId = backlog.find((c) => c.task_id.toLowerCase() === q);
        const matches = byId ? [byId] : backlog.filter((c) => c.title.toLowerCase().includes(q));
        if (matches.length === 0) return { tone: 'error', text: `No backlog item matches "${arg}". Run /backlog to see items.` };
        if (matches.length > 1) {
          return { tone: 'warn', text: `"${arg}" matches ${matches.length} items. Re-run /ready with a task id:\n${matches.map((c) => `  ${c.title} (${c.task_id})`).join('\n')}` };
        }
        const target = matches[0];
        await apiClient.moveTaskToReady(p.project_id, target.task_id);
        return { tone: 'ok', text: `Moved "${target.title}" to Ready. It is picked up by the normal heartbeat/pickup flow — the console starts no work directly.`, links: [{ label: 'View on board', to: `/projects/${p.project_id}/board` }] };
      }
      case 'runs': {
        const p = requireProject();
        if (!p) return { tone: 'warn', text: 'Select a project first with /use <project>.' };
        const runs = await apiClient.listProjectRuns(p.project_id);
        if (runs.length === 0) return { tone: 'info', text: `No orchestration runs in ${p.name}. Start one with /orchestrate <goal>.` };
        return {
          tone: 'info',
          text: `${runs.length} run(s) in ${p.name} — bind one with /monitor <runId>:`,
          links: runs.slice(0, 25).map((r) => {
            const rid = r.workflow_run_id ?? r.execution_id;
            return { label: `${r.status} · ${r.task.slice(0, 56)} (${rid})`, to: `/projects/${p.project_id}/orchestrations/${rid}` };
          }),
        };
      }
      case 'orchestrate': {
        const p = requireProject();
        if (!p) return { tone: 'warn', text: 'Select a project first with /use <project>.' };
        if (!arg) return { tone: 'warn', text: 'What goal should the orchestration pursue? Try /orchestrate <goal>.' };
        const res = await apiClient.startOrchestration(p.project_id, arg);
        return {
          tone: 'ok',
          text: `Started orchestration in ${p.name}. The coordinator will draft an Outcome plan — confirm it below (or on the run page) before work is dispatched; the gate is not bypassed. Bound the terminal to this run.`,
          links: [{ label: 'Open orchestration', to: `/projects/${p.project_id}/orchestrations/${res.runId}` }],
          bindRunId: res.runId,
          bindRunStatus: undefined,
        };
      }
      case 'monitor': {
        if (!arg) return { tone: 'warn', text: 'Which run? Try /monitor <runId> (see /runs).' };
        const rid = arg.split(/\s+/)[0];
        let status: string | undefined;
        try { status = (await apiClient.getRun(rid)).status; } catch { /* unknown → live-only, no seed */ }
        const p = requireProject();
        const links: ConsoleLink[] = p ? [{ label: 'Open run', to: `/projects/${p.project_id}/orchestrations/${rid}` }] : [];
        return { tone: 'ok', text: `Bound terminal to run ${rid}. Streaming live updates + inline gates below; durable history is seeded for parked/finished runs.`, links, bindRunId: rid, bindRunStatus: status };
      }
      case 'confirm': {
        if (!requireBound()) return { tone: 'warn', text: 'No bound run. Bind one with /monitor <runId> or /orchestrate <goal>.' };
        await model.confirmOutcomeSpec();
        return { tone: 'ok', text: 'Outcome plan confirmed. The coordinator will proceed to dispatch work.' };
      }
      case 'revise': {
        if (!requireBound()) return { tone: 'warn', text: 'No bound run. Bind one with /monitor <runId> first.' };
        if (!arg) return { tone: 'warn', text: 'Provide revision feedback: /revise <what to change>.' };
        await model.reviseOutcomeSpec(arg);
        return { tone: 'ok', text: 'Revision sent. Watch the transcript for the updated Outcome plan, then /confirm.' };
      }
      case 'approve-assembly': {
        if (!requireBound()) return { tone: 'warn', text: 'No bound run. Bind one with /monitor <runId> first.' };
        await model.reviewAssembly('approve', arg || undefined);
        return { tone: 'ok', text: 'Assembly review approved. The coordinator will merge/scribe/complete.' };
      }
      case 'stop': {
        if (!requireBound()) return { tone: 'warn', text: 'No bound run to stop.' };
        await model.stop();
        return { tone: 'warn', text: 'Stop directive sent to the coordinator.' };
      }
    }
  }, [boundRunId, model, projectLink]);

  const submit = useCallback(async () => {
    const raw = input;
    if (!raw.trim() || busy) return;
    const parsed = parseInput(raw);
    appendLine('user', raw.trim());
    setInput('');
    setBusy(true);
    try {
      if (parsed.channel === 'unknown-command') {
        appendLine('error', `Unknown command /${parsed.token}. Type /help for the command list.`);
        return;
      }
      if (parsed.channel === 'prose') {
        if (!parsed.text) return;
        // Prose confirmation of a previously-captured goal.
        if (pendingGoal && /^(y|yes|start|confirm)$/i.test(parsed.text)) {
          const p = activeProjectRef.current;
          if (!p) { appendLine('warn', 'Select a project first with /use <project>, then re-send the goal.'); return; }
          const goal = pendingGoal;
          setPendingGoal(null);
          const res = await apiClient.startOrchestration(p.project_id, goal);
          setBoundRunId(res.runId);
          setBoundRunStatus(undefined);
          appendLine('ok', `Started orchestration for: "${goal}". Confirm the Outcome plan below before work is dispatched.`, [{ label: 'Open orchestration', to: `/projects/${p.project_id}/orchestrations/${res.runId}` }]);
          return;
        }
        if (boundRunId) {
          // Conversational coordinator loop — prose goes to the REAL coordinator agent.
          await model.sendMessage(parsed.text);
          appendLine('info', 'Sent to the coordinator. Watch the run transcript below for its response.');
          return;
        }
        // No bound run → do NOT auto-start work; ask for explicit confirmation (gate).
        setPendingGoal(parsed.text);
        const hasProject = !!activeProjectRef.current;
        appendLine(
          'warn',
          hasProject
            ? `No orchestration is bound. Start one with this as the goal? Reply "yes" to start, or /monitor <runId> to attach to an existing run.\n  goal: ${parsed.text}`
            : 'No project selected and no run bound. Run /use <project> first, then /orchestrate <goal>.',
        );
        return;
      }
      // Explicit command channel.
      const result = await runCommand(parsed.name, parsed.arg);
      if (result.setActiveProject !== undefined) setActiveProject(result.setActiveProject);
      if (result.bindRunId !== undefined) { setBoundRunId(result.bindRunId); setBoundRunStatus(result.bindRunStatus); }
      appendLine(result.tone, result.text, result.links);
    } catch (err) {
      appendLine('error', errText(err));
    } finally {
      setBusy(false);
    }
  }, [input, busy, appendLine, runCommand, model, boundRunId, pendingGoal]);

  // Header quick-command chips run the exact same zero-arg command path as typing
  // it — they append the same "you typed this" line so the transcript stays the
  // single source of truth, they're just a faster on-ramp for /help, /projects, /clear.
  const runQuickCommand = useCallback(async (name: Extract<SlashCommandName, 'help' | 'projects' | 'clear'>) => {
    if (busy) return;
    appendLine('user', `/${name}`);
    setBusy(true);
    try {
      const result = await runCommand(name, '');
      if (result.setActiveProject !== undefined) setActiveProject(result.setActiveProject);
      if (result.bindRunId !== undefined) { setBoundRunId(result.bindRunId); setBoundRunStatus(result.bindRunStatus); }
      appendLine(result.tone, result.text, result.links);
    } catch (err) {
      appendLine('error', errText(err));
    } finally {
      setBusy(false);
    }
  }, [busy, appendLine, runCommand]);

  const toneClass = (tone: LineTone) => {
    switch (tone) {
      case 'error': return styles.errText;
      case 'warn': return styles.warnText;
      case 'ok': return styles.okText;
      default: return undefined;
    }
  };

  const activeLabel = useMemo(() => (activeProject ? activeProject.name : 'none'), [activeProject]);
  const gates = model.gates;

  // Runs an inline gate action (confirm / assembly review) then reports it in the transcript.
  const submitGate = useCallback((action: () => Promise<unknown>, okText: string) => {
    setBusy(true);
    return action()
      .then(() => appendLine('ok', okText))
      .catch((err) => appendLine('error', errText(err)))
      .finally(() => setBusy(false));
  }, [appendLine]);

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <div className={styles.headerCommandGroup}>
          <Text className={styles.title}>agentweaver://console</Text>
          <div className={styles.headerChips} role="group" aria-label="Quick commands">
            <Button className={styles.chip} appearance="transparent" size="small" onClick={() => { void runQuickCommand('help'); }} disabled={busy}>help</Button>
            <Button className={styles.chip} appearance="transparent" size="small" onClick={() => { void runQuickCommand('projects'); }} disabled={busy}>projects</Button>
            <Button className={styles.chip} appearance="transparent" size="small" onClick={() => { void runQuickCommand('clear'); }} disabled={busy}>clear</Button>
          </div>
        </div>
        <div className={styles.headerMeta}>
          {busy && (
            <span className={styles.busyIndicator} role="status" aria-live="polite">
              <Spinner size="tiny" /> <Text size={200}>working…</Text>
            </span>
          )}
          <Text size={200} className={styles.headerLabel}>project:</Text>
          <Badge appearance="tint" color={activeProject ? 'brand' : 'subtle'}>{activeLabel}</Badge>
          {boundRunId && <Badge appearance="tint" color="informative">{boundRunKind} · {boundRunId.slice(0, 8)}</Badge>}
        </div>
      </div>

      <div className={styles.body}>
        <div className={`${styles.transcript}${boundRunId ? ` ${styles.transcriptCapped}` : ''}`} ref={transcriptRef} aria-label="Console transcript" role="log">
          {lines.map((l, idx) => (
            <div key={l.id} className={l.tone === 'user' && idx > 0 ? styles.blockStart : undefined}>
              <div className={styles.line}>
                <span className={`${styles.gutter} ${l.tone === 'user' ? styles.promptGlyph : styles.sysGlyph}`} aria-hidden="true">
                  {l.tone === 'user' ? '❯' : '·'}
                </span>
                <span className={toneClass(l.tone)}>{l.text}</span>
              </div>
              {l.links && l.links.length > 0 && (
                <div className={styles.linkList}>
                  {l.links.map((lk, i) => (
                    <RouterLink key={`${l.id}-${i}`} to={lk.to} className={styles.link}>
                      <Open16Regular aria-hidden="true" />{lk.label}
                    </RouterLink>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>

        {boundRunId && (
          <div className={styles.runPanel} aria-label="Bound run">
            <div className={styles.runPanelHeader}>
              <Text weight="semibold" size={200}>
                run {boundRunId.slice(0, 12)} · {model.derivedRunStatus}
              </Text>
              <div className={styles.runPanelActions}>
                <Badge appearance="outline" color={model.status === 'streaming' ? 'success' : model.status === 'error' ? 'danger' : model.status === 'done' ? 'informative' : 'warning'}>
                  {model.status}
                </Badge>
                {(gates.openQuestionCount > 0 || gates.openApprovalCount > 0) && (
                  <Badge appearance="tint" color="warning" icon={<Warning16Regular />}>
                    {gates.openQuestionCount + gates.openApprovalCount} open gate(s)
                  </Badge>
                )}
                <Button size="small" appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={model.reconnect}>Reconnect</Button>
                {activeProject && (
                  <RouterLink to={`/projects/${activeProject.project_id}/orchestrations/${boundRunId}`} className={styles.link}>
                    <Open16Regular aria-hidden="true" />Full run (Changes / merge)
                  </RouterLink>
                )}
              </div>
            </div>

            {(gates.outcomeSpecPending || gates.assemblyReviewPending) && (
              <div className={styles.gateBar} role="group" aria-label="Run gates">
                {gates.outcomeSpecPending && (
                  <>
                    <Text size={200} weight="semibold">Outcome plan awaiting confirmation</Text>
                    <Button size="small" appearance="primary" icon={<CheckmarkCircle16Regular />} onClick={() => { void submitGate(() => model.confirmOutcomeSpec(), 'Outcome plan confirmed.'); }}>Confirm</Button>
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>or /revise &lt;feedback&gt;</Text>
                  </>
                )}
                {gates.assemblyReviewPending && (
                  <>
                    <Text size={200} weight="semibold">Assembly awaiting review</Text>
                    <Button size="small" appearance="primary" icon={<CheckmarkCircle16Regular />} onClick={() => { void submitGate(() => model.reviewAssembly('approve'), 'Assembly approved.'); }}>Approve</Button>
                    <Button size="small" appearance="secondary" onClick={() => { void submitGate(() => model.reviewAssembly('request_changes'), 'Requested changes on the assembly.'); }}>Request changes</Button>
                    <Button size="small" appearance="subtle" onClick={() => { void submitGate(() => model.reviewAssembly('decline'), 'Assembly declined.'); }}>Decline</Button>
                  </>
                )}
              </div>
            )}

            <div className={styles.timelineScroll} ref={timelineScrollRef} onScroll={onTimelineScroll}>
              <Timeline
                items={model.items}
                streamStatus={model.status}
                isLiveRun={model.isLiveRun}
                runId={boundRunId}
                runOutcome={model.runOutcome}
                skippedEventCount={model.droppedEventCount}
              />
            </div>
          </div>
        )}
      </div>

      <div className={styles.promptLine}>
        <span className={styles.promptContext} aria-hidden="true">
          <span className={styles.promptPath}>~\Git\agentweaver</span>
          {' '}
          <span className={styles.promptBranch}>[{activeProject ? activeProject.name : 'integration'}]</span>
          <span className={styles.promptCaret}>❯</span>
        </span>
        <Textarea
          className={styles.input}
          appearance="filled-lighter"
          value={input}
          placeholder={boundRunId ? 'Message the coordinator, or /command …' : 'Type /help, /projects, /orchestrate <goal> — or prose to start a conversation'}
          onChange={(_, d) => setInput(d.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
          disabled={busy}
          resize="none"
          aria-label="Console input"
        />
        {!input && !busy && <span className={`${styles.promptCaret} ${styles.blink}`} aria-hidden="true">▋</span>}
        <span className={styles.promptHint} aria-hidden="true">⏎ send · ⇧⏎ newline</span>
        <Button className={styles.sendBtn} size="small" appearance="subtle" icon={busy ? <Spinner size="tiny" /> : <Send16Regular />} disabled={busy || !input.trim()} onClick={() => void submit()}>
          Send
        </Button>
      </div>
    </div>
  );
}
