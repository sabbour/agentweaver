import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { DEFERRED_COMMANDS, parseInput, SLASH_COMMANDS } from './consoleCommands';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { parseNoTeamStartError } from '../api/errors';
import type {
  AgentweaverConsoleResponse,
  AgentweaverConsoleToolCall,
  BoardColumnDto,
  Project,
  TaskCardDto,
} from '../api/types';
import type { SlashCommandName } from './consoleCommands';
import type { CSSProperties } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { SparkleRegular } from '@fluentui/react-icons';

import { CopilotChat, CopilotMessage, UserMessage, Composer } from '../components/ui/copilot';
import { AgentStepList } from '../components/ui/agentic';
import type { AgentStep, AgentStepStatus } from '../components/ui/agentic';
import { EmptyState } from '../components/ui';

// Singleton sidecar console. Natural language goes to the backend Agentweaver
// facade; slash commands are secondary shortcuts over the same API client surface.

const CONSOLE_ROOT_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  background: 'var(--colorNeutralBackground2)',
};

const TRANSCRIPT_STYLE: CSSProperties = {
  flex: '1 1 auto',
  minHeight: 0,
  overflowY: 'auto',
  display: 'flex',
  flexDirection: 'column',
};

const COMPOSER_WRAP_STYLE: CSSProperties = {
  flexShrink: 0,
  padding: 'var(--spacingVerticalM) var(--spacingHorizontalL)',
  borderTop: '1px solid var(--colorNeutralStroke2)',
  background: 'var(--colorNeutralBackground1)',
};

const MESSAGE_TEXT_STYLE: CSSProperties = {
  whiteSpace: 'pre-wrap',
  overflowWrap: 'anywhere',
};

const useConsoleStyles = makeStyles({
  contextHeader: {
    flexShrink: 0,
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  contextLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase200,
  },
  contextBadge: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    lineHeight: tokens.lineHeightBase200,
  },
  contextSep: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
    lineHeight: tokens.lineHeightBase200,
  },
  shortcutStrip: {
    flexShrink: 0,
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalL}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  shortcutButton: {
    background: 'none',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    padding: `2px ${tokens.spacingHorizontalS}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    cursor: 'pointer',
    lineHeight: tokens.lineHeightBase200,
    transition: 'background-color 100ms ease',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3,
      color: tokens.colorNeutralForeground1,
    },
    ':disabled': {
      opacity: '0.5',
      cursor: 'default',
    },
  },
  composerHint: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
  },
  statusWarning: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase200,
    color: 'var(--colorPaletteYellowForeground2)',
  },
  statusDanger: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
  },
  statusNote: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  linkList: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXS,
  },
  link: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'underline',
    ':hover': {
      color: tokens.colorNeutralForeground3,
    },
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

function consoleRouteContext(pathname: string): { projectId?: string; runId?: string; scope: ConsoleScope } {
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

function toolStepStatus(status: AgentweaverConsoleToolCall['status']): AgentStepStatus {
  if (status === 'completed') return 'complete';
  if (status === 'queued' || status === 'running') return 'running';
  if (status === 'failed') return 'blocked';
  return 'warning';
}

function toolSteps(tools?: AgentweaverConsoleToolCall[]): AgentStep[] {
  return (tools ?? []).map((tool, index) => ({
    id: `${tool.name}-${index}`,
    title: tool.name,
    body: tool.summary,
    status: toolStepStatus(tool.status),
    defaultOpen: Boolean(tool.summary),
  }));
}

function messageName(message: ConsoleMessage): string {
  if (message.kind === 'clarification') return 'Clarification needed';
  if (message.kind === 'gate') return message.gateTitle ?? 'Confirmation required';
  if (message.kind === 'error') return message.gateTitle ?? 'Console error';
  if (message.kind === 'tool') return 'Action summary';
  return 'Agentweaver';
}

function ConsoleLinks({ links }: { links?: ConsoleLink[] }) {
  const styles = useConsoleStyles();
  if (!links?.length) return null;
  return (
    <div className={styles.linkList} aria-label="Related links">
      {links.map((link, index) => link.to ? (
        <RouterLink key={`${link.label}-${index}`} className={styles.link} to={link.to}>
          {link.label}
        </RouterLink>
      ) : link.href ? (
        <a key={`${link.label}-${index}`} className={styles.link} href={link.href} target="_blank" rel="noreferrer">
          {link.label}
        </a>
      ) : null)}
    </div>
  );
}

function MessageContent({ message }: { message: ConsoleMessage }) {
  const styles = useConsoleStyles();
  const steps = toolSteps(message.tools);
  return (
    <div data-kind={message.kind}>
      {message.text && <div style={MESSAGE_TEXT_STYLE}>{message.text}</div>}
      {message.kind === 'gate' && (
        <div className={styles.statusWarning}>
          Review or confirmation is still required before work proceeds.
        </div>
      )}
      {message.kind === 'clarification' && (
        <div className={styles.statusWarning}>
          Answer the clarification before Agentweaver continues.
        </div>
      )}
      {message.kind === 'error' && (
        <div className={styles.statusDanger}>
          The request needs attention before it can continue.
        </div>
      )}
      {steps.length > 0 && (
        <AgentStepList steps={steps} aria-label="Tool actions" />
      )}
      <ConsoleLinks links={message.links} />
    </div>
  );
}

export function BrowserConsole() {
  const location = useLocation();
  const route = useMemo(() => consoleRouteContext(location.pathname), [location.pathname]);
  const [messages, setMessages] = useState<ConsoleMessage[]>([WELCOME]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [selectedProject, setSelectedProject] = useState<Project | null>(null);
  const [routeProjectState, setRouteProjectState] = useState<{ projectId?: string; project: Project | null }>({ project: null });
  const [boundRunId, setBoundRunId] = useState('');
  const [conversationId, setConversationId] = useState<string | undefined>();
  const streamRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = streamRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, busy]);

  useEffect(() => {
    if (!route.projectId) return;
    let cancelled = false;
    apiClient.getProject(route.projectId)
      .then((project) => {
        if (!cancelled) setRouteProjectState({ projectId: route.projectId, project });
      })
      .catch(() => {
        if (!cancelled) setRouteProjectState({ projectId: route.projectId, project: null });
      });
    return () => { cancelled = true; };
  }, [route.projectId]);

  useEffect(() => {
    if (route.runId) queueMicrotask(() => setBoundRunId(route.runId ?? ''));
  }, [route.runId]);

  const routeProject = routeProjectState.projectId === route.projectId ? routeProjectState.project : null;
  const effectiveProjectId = route.projectId ?? selectedProject?.project_id;
  const effectiveProjectName = route.projectId
    ? (routeProject?.name ?? route.projectId)
    : (selectedProject?.name ?? undefined);
  const effectiveRunId = route.runId ?? (boundRunId || undefined);
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
        let res: Awaited<ReturnType<typeof apiClient.startOrchestration>>;
        try {
          res = await apiClient.startOrchestration(projectId, arg);
        } catch (err) {
          const noTeam = parseNoTeamStartError(err);
          if (noTeam) {
            return {
              kind: 'error',
              text: noTeam.message,
              links: [{ label: 'Cast a team', to: `/projects/${projectId}/team/cast` }],
              tools: [commandTool('Start orchestration')],
            };
          }
          throw err;
        }
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

  const shortcutPrompts = useMemo(() => [
    { id: 'help', label: '/help', onClick: () => runQuickCommand('help') },
    { id: 'projects', label: '/projects', onClick: () => runQuickCommand('projects') },
    { id: 'runs', label: '/runs', onClick: () => runQuickCommand('runs') },
    { id: 'clear', label: '/clear', onClick: () => runQuickCommand('clear') },
  ], [runQuickCommand]);

  const styles = useConsoleStyles();
  const isInitialState = messages.length === 1 && messages[0].id === 'welcome';

  return (
    <div style={CONSOLE_ROOT_STYLE} data-testid="browser-console">
      <span style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0,0,0,0)' }}>
        Agentweaver Console
      </span>

      {/* Context header — scope + project + run identifiers */}
      <div className={styles.contextHeader}>
        <SparkleRegular aria-hidden="true" fontSize={14} />
        <span className={styles.contextLabel}>Agentweaver</span>
        <span className={styles.contextSep}>·</span>
        <span className={styles.contextBadge}>{scopeLabel}</span>
        {effectiveProjectId && (
          <>
            <span className={styles.contextSep}>·</span>
            <span className={styles.contextBadge}>Project · {effectiveProjectBadgeLabel}</span>
          </>
        )}
        {effectiveRunId && (
          <>
            <span className={styles.contextSep}>·</span>
            <span className={styles.contextBadge}>Run · {effectiveRunId.slice(0, 12)}</span>
          </>
        )}
        {conversationId && (
          <>
            <span className={styles.contextSep}>·</span>
            <span className={styles.contextBadge}>Conversation · {conversationId}</span>
          </>
        )}
      </div>

      {/* Chat feed */}
      <div
        ref={streamRef}
        style={TRANSCRIPT_STYLE}
        role="log"
        aria-live="polite"
        aria-relevant="additions text"
        aria-label="Console responses"
        aria-busy={busy || undefined}
      >
        {isInitialState ? (
          <CopilotChat style={{ overflowY: 'visible', flex: '1 0 auto' }}>
            <EmptyState
              icon={<SparkleRegular />}
              title="Ask Agentweaver"
              description={WELCOME.text}
            />
          </CopilotChat>
        ) : (
          <CopilotChat style={{ overflowY: 'visible', flex: '1 0 auto' }}>
            {messages.map((msg) =>
              msg.role === 'user' ? (
                <UserMessage key={msg.id} accessibleHeading={`You: ${msg.text}`}>
                  <div style={MESSAGE_TEXT_STYLE}>{msg.text}</div>
                </UserMessage>
              ) : (
                <CopilotMessage
                  key={msg.id}
                  name={messageName(msg)}
                  loadingState="none"
                >
                  <MessageContent message={msg} />
                </CopilotMessage>
              )
            )}
            {busy && (
              <CopilotMessage name="Agentweaver" loadingState="loading" />
            )}
          </CopilotChat>
        )}
      </div>

      {/* Shortcut strip */}
      <div className={styles.shortcutStrip} aria-label="Console shortcuts">
        {shortcutPrompts.map((s) => (
          <button
            key={s.id}
            type="button"
            className={styles.shortcutButton}
            onClick={s.onClick}
            disabled={busy}
          >
            {s.label}
          </button>
        ))}
      </div>

      {/* Composer */}
      <div style={COMPOSER_WRAP_STYLE}>
        <Composer
          value={input}
          onChange={setInput}
          onSubmit={() => void submit()}
          onStop={() => { /* stop not yet wired to backend */ }}
          isSending={busy}
          placeholder="Ask Agentweaver"
        />
        <div className={styles.composerHint}>
          Enter to send · Shift+Enter for new line · slash shortcuts optional
        </div>
      </div>
    </div>
  );
}
