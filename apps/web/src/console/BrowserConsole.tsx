import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import {
  Badge,
  Button,
  Spinner,
  Text,
  Textarea,
  makeStyles,
  mergeClasses,
  shorthands,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircle16Regular,
  Open16Regular,
  Send16Regular,
  Warning16Regular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  AgentweaverConsoleResponse,
  AgentweaverConsoleToolCall,
  BoardColumnDto,
  Project,
  TaskCardDto,
} from '../api/types';
import {
  DEFERRED_COMMANDS,
  SLASH_COMMANDS,
  parseInput,
  type SlashCommandName,
} from './consoleCommands';

// Smart operator dock for the singleton browser console. Natural language goes to
// the backend Agentweaver facade; slash commands remain secondary shortcuts over
// the same typed API client surface.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
  },
  header: {
    flexShrink: 0,
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalL),
    ...shorthands.borderBottom('1px', 'solid', tokens.colorNeutralStroke2),
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 720px)': { gridTemplateColumns: '1fr' },
  },
  titleStack: {
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  contextRail: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  quickRow: {
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    ...shorthands.padding(tokens.spacingVerticalXS, tokens.spacingHorizontalL),
    ...shorthands.borderBottom('1px', 'solid', tokens.colorNeutralStroke2),
    backgroundColor: tokens.colorNeutralBackground1,
  },
  quickLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
  },
  stream: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalL),
  },
  turn: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    alignItems: 'start',
  },
  avatar: {
    width: '28px',
    height: '28px',
    borderRadius: tokens.borderRadiusCircular,
    display: 'grid',
    placeItems: 'center',
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightSemibold,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
  },
  userAvatar: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
    borderTopColor: tokens.colorBrandStroke2,
    borderRightColor: tokens.colorBrandStroke2,
    borderBottomColor: tokens.colorBrandStroke2,
    borderLeftColor: tokens.colorBrandStroke2,
  },
  message: {
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  messageMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  author: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
  },
  roleLabel: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  bubble: {
    maxWidth: '78ch',
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    lineHeight: tokens.lineHeightBase300,
    fontSize: tokens.fontSizeBase300,
  },
  userBubble: {
    color: tokens.colorNeutralForeground1,
  },
  stateBlock: {
    maxWidth: '78ch',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalM),
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: tokens.colorNeutralBackground2,
  },
  clarificationBlock: {
    borderTopColor: tokens.colorStatusWarningBorder1,
    borderRightColor: tokens.colorStatusWarningBorder1,
    borderBottomColor: tokens.colorStatusWarningBorder1,
    borderLeftColor: tokens.colorStatusWarningBorder1,
    backgroundColor: tokens.colorStatusWarningBackground1,
  },
  gateBlock: {
    borderTopColor: tokens.colorPaletteMarigoldBorderActive,
    borderRightColor: tokens.colorPaletteMarigoldBorderActive,
    borderBottomColor: tokens.colorPaletteMarigoldBorderActive,
    borderLeftColor: tokens.colorPaletteMarigoldBorderActive,
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
  },
  errorBlock: {
    borderTopColor: tokens.colorStatusDangerBorder1,
    borderRightColor: tokens.colorStatusDangerBorder1,
    borderBottomColor: tokens.colorStatusDangerBorder1,
    borderLeftColor: tokens.colorStatusDangerBorder1,
    backgroundColor: tokens.colorStatusDangerBackground1,
  },
  stateTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontWeight: tokens.fontWeightSemibold,
  },
  toolList: {
    maxWidth: '78ch',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    marginTop: tokens.spacingVerticalXXS,
  },
  toolRow: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    alignItems: 'baseline',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  links: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  link: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'none',
    fontSize: tokens.fontSizeBase200,
    minHeight: '24px',
    ':hover': { textDecorationLine: 'underline' },
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineColor: tokens.colorStrokeFocus2,
      outlineOffset: '2px',
    },
  },
  composer: {
    flexShrink: 0,
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: tokens.spacingHorizontalS,
    alignItems: 'end',
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalL),
    ...shorthands.borderTop('1px', 'solid', tokens.colorNeutralStroke2),
    backgroundColor: tokens.colorNeutralBackground1,
  },
  input: {
    '& textarea': {
      minHeight: '48px',
      maxHeight: '140px',
      lineHeight: tokens.lineHeightBase300,
    },
  },
  composerHint: {
    gridColumn: '1 / -1',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
});

type ConsoleScope = 'global' | 'project' | 'run';
type MessageKind = 'answer' | 'tool' | 'clarification' | 'gate' | 'error';

interface ConsoleLink { label: string; to?: string; href?: string }
interface ConsoleMessage {
  id: string;
  role: 'user' | 'assistant';
  kind: MessageKind;
  text: string;
  links?: ConsoleLink[];
  tools?: AgentweaverConsoleToolCall[];
  gateTitle?: string;
}

interface CommandResult {
  kind?: MessageKind;
  text: string;
  links?: ConsoleLink[];
  tools?: AgentweaverConsoleToolCall[];
  setSelectedProject?: Project | null;
  bindRunId?: string;
  clear?: boolean;
}

let seq = 0;
const nextId = () => `console-${Date.now()}-${seq++}`;

export function consoleRouteContext(pathname: string): { projectId?: string; runId?: string; scope: ConsoleScope } {
  const runMatch = /^\/projects\/([^/]+)\/orchestrations\/([^/]+)/.exec(pathname);
  if (runMatch) return { projectId: decodeURIComponent(runMatch[1]), runId: decodeURIComponent(runMatch[2]), scope: 'run' };
  const projectMatch = /^\/projects\/([^/]+)/.exec(pathname);
  if (projectMatch) return { projectId: decodeURIComponent(projectMatch[1]), scope: 'project' };
  return { scope: 'global' };
}

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
  const shortcuts = SLASH_COMMANDS
    .map((c) => `/${c.name}${c.argHint ? ` ${c.argHint}` : ''} — ${c.summary}`)
    .join('\n');
  const deferred = DEFERRED_COMMANDS.map((c) => `${c.label} — ${c.summary}`).join('\n');
  return `Natural language is the primary interface. Ask Agentweaver what you want; the facade agent selects tools and asks only when needed.\n\nSecondary shortcuts:\n${shortcuts}\n\nGated surfaces stay gated:\n${deferred}`;
}

const WELCOME: ConsoleMessage = {
  id: 'welcome',
  role: 'assistant',
  kind: 'answer',
  text: 'Ask Agentweaver to inspect projects, start or monitor orchestrations, summarize runs, or help unblock work. I will route requests through the facade agent and surface gates for review.',
};

function commandTool(name: string, status: AgentweaverConsoleToolCall['status'] = 'completed', summary?: string): AgentweaverConsoleToolCall {
  return { name, status, summary };
}

function responseLinks(response: AgentweaverConsoleResponse): ConsoleLink[] {
  const links: ConsoleLink[] = (response.links ?? []).map((link) => ({
    label: link.label,
    to: link.to,
    href: link.href,
  }));
  if (response.project_id && !links.some((l) => l.to === `/projects/${response.project_id}`)) {
    links.push({ label: 'Open project', to: `/projects/${response.project_id}` });
  }
  if (response.project_id && response.run_id && !links.some((l) => l.to === `/projects/${response.project_id}/orchestrations/${response.run_id}`)) {
    links.push({ label: 'Open run', to: `/projects/${response.project_id}/orchestrations/${response.run_id}` });
  }
  return links;
}

function responseToMessage(response: AgentweaverConsoleResponse): ConsoleMessage {
  const kind: MessageKind = response.status === 'needs_clarification'
      || response.kind === 'clarification'
      || Boolean(response.clarifications?.length)
      ? 'clarification'
      : response.status === 'needs_confirmation'
      || response.kind === 'gate_required'
      || Boolean(response.gate)
        ? 'gate'
      : response.status === 'blocked'
      || response.kind === 'error'
      || Boolean(response.errors?.length)
        ? 'error'
          : response.tool_calls?.length || response.tools?.length || response.action_summaries?.length || response.action
            ? 'tool'
            : 'answer';
  const tools = response.tool_calls?.length
      ? response.tool_calls
      : response.tools?.length
        ? response.tools.map((tool) => ({ name: tool.label, status: tool.status, summary: tool.detail }))
      : response.action_summaries?.length
        ? response.action_summaries.map((action) => ({ name: action.label ?? action.action, status: action.status, summary: action.detail }))
      : response.action
        ? [{ name: response.action, status: response.status ?? 'completed', summary: response.message }]
        : undefined;
  const text = response.message
      || response.message_chunks?.map((chunk) => chunk.text).join('')
      || response.clarifications?.map((item) => item.prompt).join('\n')
      || response.errors?.map((item) => item.message).join('\n')
      || '';
  return {
      id: nextId(),
      role: 'assistant',
      kind,
      text,
      links: responseLinks(response),
      tools,
      gateTitle: response.status === 'blocked' ? 'Blocked' : response.gate?.title ?? response.action ?? undefined,
  };
}

function linkFromProject(p: Project): ConsoleLink {
  return { label: `Open ${p.name}`, to: `/projects/${p.project_id}` };
}

export function BrowserConsole() {
  const styles = useStyles();
  const location = useLocation();
  const route = useMemo(() => consoleRouteContext(location.pathname), [location.pathname]);
  const [messages, setMessages] = useState<ConsoleMessage[]>([WELCOME]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [selectedProject, setSelectedProject] = useState<Project | null>(null);
  const [routeProject, setRouteProject] = useState<Project | null>(null);
  const [boundRunId, setBoundRunId] = useState('');
  const [conversationId, setConversationId] = useState<string | undefined>();
  const streamRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = streamRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, busy]);

  useEffect(() => {
    if (!route.projectId) {
      setRouteProject(null);
      return;
    }
    let cancelled = false;
    apiClient.getProject(route.projectId)
      .then((project) => { if (!cancelled) setRouteProject(project); })
      .catch(() => { if (!cancelled) setRouteProject(null); });
    return () => { cancelled = true; };
  }, [route.projectId]);

  useEffect(() => {
    if (route.runId) setBoundRunId(route.runId);
  }, [route.runId]);

  const effectiveProjectId = route.projectId ?? selectedProject?.project_id;
  const effectiveProjectName = route.projectId
    ? (routeProject?.name ?? route.projectId)
    : (selectedProject?.name ?? undefined);
  const effectiveRunId = route.runId ?? (boundRunId ? boundRunId : undefined);
  const effectiveScope: ConsoleScope = effectiveRunId ? 'run' : effectiveProjectId ? 'project' : 'global';

  const append = useCallback((message: Omit<ConsoleMessage, 'id'>) => {
    setMessages((prev) => [...prev, { ...message, id: nextId() }]);
  }, []);

  const runCommand = useCallback(async (name: SlashCommandName, arg: string): Promise<CommandResult> => {
    const projectId = effectiveProjectId;
    const runId = effectiveRunId;
    switch (name) {
      case 'help':
        return { text: buildHelp() };
      case 'clear':
        return { text: '', clear: true };
      case 'projects': {
        const projects = await apiClient.listProjects();
        if (!projects.length) return { text: 'No projects yet.', links: [{ label: 'Open Projects', to: '/projects' }], tools: [commandTool('List projects')] };
        return {
          text: `${projects.length} project(s). Use /use <name or id> to pin one when you are outside a project route.`,
          links: projects.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` })),
          tools: [commandTool('List projects')],
        };
      }
      case 'use': {
        if (!arg) return { kind: 'clarification', text: 'Which project should I use? Try /projects, then /use <name or id>.' };
        const projects = await apiClient.listProjects();
        const { project, candidates } = resolveProject(projects, arg);
        if (project) {
          return {
            text: `Pinned ${project.name} for global console requests. Project routes still override this context while you are on them.`,
            links: [linkFromProject(project)],
            tools: [commandTool('Select project')],
            setSelectedProject: project,
          };
        }
        if (candidates?.length) {
          return {
            kind: 'clarification',
            text: `“${arg}” matches ${candidates.length} projects. Choose the exact id with /use <id>.`,
            links: candidates.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` })),
          };
        }
        return { kind: 'error', text: `No project matches “${arg}”.` };
      }
      case 'backlog': {
        if (!projectId) return { kind: 'clarification', text: 'Choose a project first with /use <project>, or navigate to a project.' };
        const board = await apiClient.getBoard(projectId);
        const cards = intakeCards(board.columns);
        return {
          text: cards.length ? cards.map((c) => `${c.state}: ${c.title} (${c.task_id})`).join('\n') : 'No backlog or ready items found.',
          links: [{ label: 'Open board', to: `/projects/${projectId}/board` }],
          tools: [commandTool('Read board')],
        };
      }
      case 'add': {
        if (!projectId) return { kind: 'clarification', text: 'Choose a project first with /use <project>, or navigate to a project.' };
        if (!arg) return { kind: 'clarification', text: 'What backlog item should I capture? Use /add <title> :: <optional description>.' };
        const [titlePart, ...rest] = arg.split('::');
        const title = titlePart.trim();
        if (!title) return { kind: 'clarification', text: 'The backlog item needs a title.' };
        const description = rest.join('::').trim() || null;
        const task = await apiClient.captureBacklogTask(projectId, { title, description });
        return {
          text: `Captured “${task.title}” (${task.task_id}).`,
          links: [{ label: 'Open board', to: `/projects/${projectId}/board` }],
          tools: [commandTool('Capture backlog task')],
        };
      }
      case 'ready': {
        if (!projectId) return { kind: 'clarification', text: 'Choose a project first with /use <project>, or navigate to a project.' };
        if (!arg) return { kind: 'clarification', text: 'Which backlog item should move to Ready? Use /ready <task title or id>.' };
        const board = await apiClient.getBoard(projectId);
        const backlog = intakeCards(board.columns).filter((c) => c.state === 'backlog');
        const q = arg.toLowerCase();
        const match = backlog.find((c) => c.task_id.toLowerCase() === q) ?? backlog.find((c) => c.title.toLowerCase().includes(q));
        if (!match) return { kind: 'error', text: `No backlog item matches “${arg}”.` };
        await apiClient.moveTaskToReady(projectId, match.task_id);
        return { text: `Moved “${match.title}” to Ready.`, links: [{ label: 'Open board', to: `/projects/${projectId}/board` }], tools: [commandTool('Move task to ready')] };
      }
      case 'runs': {
        if (!projectId) return { kind: 'clarification', text: 'Choose a project first with /use <project>, or navigate to a project.' };
        const runs = await apiClient.listProjectRuns(projectId);
        return {
          text: runs.length ? `${runs.length} run(s). Use /monitor <runId> or open a run.` : 'No orchestration runs yet.',
          links: runs.slice(0, 25).map((r) => {
            const id = r.workflow_run_id ?? r.execution_id;
            return { label: `${r.status} · ${r.task || id}`, to: `/projects/${projectId}/orchestrations/${id}` };
          }),
          tools: [commandTool('List project runs')],
        };
      }
      case 'orchestrate': {
        if (!projectId) return { kind: 'clarification', text: 'Choose a project first with /use <project>, or navigate to a project.' };
        if (!arg) return { kind: 'clarification', text: 'What should the orchestration accomplish?' };
        const res = await apiClient.startOrchestration(projectId, arg);
        return {
          kind: 'gate',
          text: 'Started orchestration. Review the outcome plan gate before work dispatches.',
          links: [{ label: 'Open orchestration', to: `/projects/${projectId}/orchestrations/${res.runId}` }],
          tools: [commandTool('Start orchestration')],
          bindRunId: res.runId,
        };
      }
      case 'monitor': {
        if (!arg) return { kind: 'clarification', text: 'Which run should I bind? Use /monitor <runId>.' };
        const id = arg.split(/\s+/)[0];
        return {
          text: `Bound the dock context to run ${id}.`,
          links: projectId ? [{ label: 'Open run', to: `/projects/${projectId}/orchestrations/${id}` }] : undefined,
          tools: [commandTool('Bind run')],
          bindRunId: id,
        };
      }
      case 'confirm': {
        if (!runId) return { kind: 'gate', text: 'No run is bound. Open a run or use /monitor <runId> before confirming a gate.' };
        await apiClient.confirmOutcomeSpec(runId);
        return { text: 'Outcome plan confirmed.', tools: [commandTool('Confirm outcome plan')] };
      }
      case 'revise': {
        if (!runId) return { kind: 'gate', text: 'No run is bound. Open a run or use /monitor <runId> before revising a gate.' };
        if (!arg) return { kind: 'clarification', text: 'What should change? Use /revise <feedback>.' };
        await apiClient.reviseOutcomeSpec(runId, arg);
        return { text: 'Revision sent to the coordinator.', tools: [commandTool('Revise outcome plan')] };
      }
      case 'approve-assembly': {
        if (!runId) return { kind: 'gate', text: 'No run is bound. Open a run or use /monitor <runId> before reviewing assembly.' };
        await apiClient.reviewAssembly(runId, 'approve', arg || undefined);
        return { text: 'Assembly approved.', tools: [commandTool('Review assembly')] };
      }
      case 'stop': {
        if (!runId) return { kind: 'clarification', text: 'No run is bound. Open a run or use /monitor <runId>.' };
        await apiClient.steerCoordinator(runId, { kind: 'stop' });
        return { text: 'Stop directive sent to the coordinator.', tools: [commandTool('Stop orchestration', 'queued')] };
      }
    }
  }, [effectiveProjectId, effectiveRunId]);

  const submit = useCallback(async () => {
    const raw = input.trim();
    if (!raw || busy) return;
    const parsed = parseInput(raw);
    append({ role: 'user', kind: 'answer', text: raw });
    setInput('');
    setBusy(true);
    try {
      if (parsed.channel === 'unknown-command') {
        append({ role: 'assistant', kind: 'error', text: `Unknown shortcut /${parsed.token}. Type /help for the shortcut list, or ask in natural language.` });
        return;
      }
      if (parsed.channel === 'command') {
        const result = await runCommand(parsed.name, parsed.arg);
        if (result.clear) {
          setMessages([WELCOME]);
          return;
        }
        if (result.setSelectedProject !== undefined) setSelectedProject(result.setSelectedProject);
        if (result.bindRunId !== undefined) setBoundRunId(result.bindRunId);
        append({ role: 'assistant', kind: result.kind ?? (result.tools?.length ? 'tool' : 'answer'), text: result.text, links: result.links, tools: result.tools });
        return;
      }

      const response = await apiClient.sendConsoleMessage({
        message: parsed.text,
        text: parsed.text,
        conversation_id: conversationId ?? null,
        context: {
          scope: effectiveScope,
          project_id: effectiveProjectId ?? null,
          run_id: effectiveRunId ?? null,
          route: location.pathname,
        },
      });
      if (response.conversation_id) setConversationId(response.conversation_id);
      if (response.run_id) setBoundRunId(response.run_id);
      append(responseToMessage(response));
    } catch (err) {
      append({ role: 'assistant', kind: 'error', text: errText(err) });
    } finally {
      setBusy(false);
    }
  }, [append, busy, conversationId, effectiveProjectId, effectiveRunId, effectiveScope, input, location.pathname, runCommand]);

  const runQuickCommand = useCallback((command: SlashCommandName) => {
    if (busy) return;
    setInput(`/${command}`);
  }, [busy]);

  const scopeLabel = effectiveScope === 'run' ? 'Run' : effectiveScope === 'project' ? 'Project' : 'Global';
  const effectiveProjectBadgeLabel = effectiveProjectName ?? effectiveProjectId;
  const routeBindingLabel = route.runId
    ? 'route run'
    : route.projectId
      ? 'route project'
      : selectedProject
        ? 'selected project'
        : 'global';

  const renderLinks = (links?: ConsoleLink[]) => links?.length ? (
    <div className={styles.links}>
      {links.map((link, index) => link.to ? (
        <RouterLink key={`${link.label}-${index}`} className={styles.link} to={link.to}>
          <Open16Regular aria-hidden="true" />{link.label}
        </RouterLink>
      ) : link.href ? (
        <a key={`${link.label}-${index}`} className={styles.link} href={link.href} target="_blank" rel="noreferrer">
          <Open16Regular aria-hidden="true" />{link.label}
        </a>
      ) : null)}
    </div>
  ) : null;

  const renderTools = (tools?: AgentweaverConsoleToolCall[]) => tools?.length ? (
    <div className={styles.toolList} aria-label="Action summary">
      {tools.map((tool, index) => (
        <div key={`${tool.name}-${index}`} className={styles.toolRow}>
          <Badge appearance="outline" color={tool.status === 'failed' ? 'danger' : tool.status === 'queued' ? 'warning' : 'success'}>{tool.status}</Badge>
          <span>{tool.name}{tool.summary ? ` — ${tool.summary}` : ''}</span>
        </div>
      ))}
    </div>
  ) : null;

  const stateBlockClass = (kind: MessageKind) => mergeClasses(
    styles.stateBlock,
    kind === 'clarification' && styles.clarificationBlock,
    kind === 'gate' && styles.gateBlock,
    kind === 'error' && styles.errorBlock,
  );

  return (
    <div className={styles.root} data-testid="browser-console">
      <div className={styles.header}>
        <div className={styles.titleStack}>
          <div className={styles.titleRow}>
            <Text className={styles.title}>Agentweaver Console</Text>
            {busy && <Spinner size="extra-tiny" label="Agentweaver is working" />}
          </div>
          <Text className={styles.subtitle}>Message-first operator dock. The facade agent chooses tools and surfaces gates when human review is required.</Text>
        </div>
        <div className={styles.contextRail} aria-label="Console context">
          <Badge appearance="filled" color={effectiveScope === 'global' ? 'subtle' : effectiveScope === 'project' ? 'brand' : 'informative'}>{scopeLabel}</Badge>
          {effectiveProjectId && <Badge appearance="outline">Project · {effectiveProjectBadgeLabel}</Badge>}
          {effectiveRunId && <Badge appearance="outline">Run · {effectiveRunId.slice(0, 12)}</Badge>}
          <Badge appearance="tint" color="subtle">{routeBindingLabel}</Badge>
        </div>
      </div>

      <div className={styles.quickRow} role="toolbar" aria-label="Console shortcuts">
        <Text className={styles.quickLabel}>Shortcuts</Text>
        <Button size="small" appearance="subtle" onClick={() => runQuickCommand('help')} disabled={busy}>/help</Button>
        <Button size="small" appearance="subtle" onClick={() => runQuickCommand('projects')} disabled={busy}>/projects</Button>
        <Button size="small" appearance="subtle" onClick={() => runQuickCommand('runs')} disabled={busy}>/runs</Button>
        <Button size="small" appearance="subtle" onClick={() => runQuickCommand('clear')} disabled={busy}>/clear</Button>
      </div>

      <div className={styles.stream} ref={streamRef} role="log" aria-label="Console responses">
        {messages.map((message) => {
          const isUser = message.role === 'user';
          const stateful = !isUser && (message.kind === 'clarification' || message.kind === 'gate' || message.kind === 'error');
          return (
            <div key={message.id} className={styles.turn}>
              <div className={mergeClasses(styles.avatar, isUser && styles.userAvatar)} aria-hidden="true">{isUser ? 'You' : 'AW'}</div>
              <div className={styles.message}>
                <div className={styles.messageMeta}>
                  <Text className={styles.author}>{isUser ? 'You' : 'Agentweaver'}</Text>
                  <Text className={styles.roleLabel}>{isUser ? 'request' : message.kind === 'tool' ? 'action summary' : message.kind.replace('_', ' ')}</Text>
                </div>
                {stateful ? (
                  <div className={stateBlockClass(message.kind)}>
                    <div className={styles.stateTitle}>
                      {message.kind === 'gate' ? <CheckmarkCircle16Regular aria-hidden="true" /> : <Warning16Regular aria-hidden="true" />}
                      <span>{message.kind === 'clarification' ? 'Clarification needed' : message.kind === 'gate' ? (message.gateTitle ?? 'Confirmation required') : (message.gateTitle ?? 'Console error')}</span>
                    </div>
                    <Text className={styles.bubble}>{message.text}</Text>
                  </div>
                ) : (
                  <Text className={mergeClasses(styles.bubble, isUser && styles.userBubble)}>{message.text}</Text>
                )}
                {renderTools(message.tools)}
                {renderLinks(message.links)}
              </div>
            </div>
          );
        })}
        {busy && (
          <div className={styles.turn} aria-live="polite">
            <div className={styles.avatar} aria-hidden="true">AW</div>
            <div className={styles.message}>
              <div className={styles.messageMeta}><Text className={styles.author}>Agentweaver</Text><Text className={styles.roleLabel}>working</Text></div>
              <Text className={styles.bubble}>Thinking through the request and selecting tools…</Text>
            </div>
          </div>
        )}
      </div>

      <div className={styles.composer}>
        <Textarea
          className={styles.input}
          value={input}
          placeholder="Ask Agentweaver…"
          aria-label="Ask Agentweaver"
          resize="vertical"
          disabled={busy}
          onChange={(_, data) => setInput(data.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              void submit();
            }
          }}
        />
        <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <Send16Regular />} disabled={busy || !input.trim()} onClick={() => void submit()}>
          Send
        </Button>
        <Text className={styles.composerHint}>Enter sends · Shift+Enter adds a line · slash commands are optional shortcuts, not the main workflow.</Text>
      </div>
    </div>
  );
}
