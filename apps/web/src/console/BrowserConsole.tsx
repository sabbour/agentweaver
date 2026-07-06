import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import {
  Badge,
  Body1,
  Button,
  Caption1,
  Card,
  Divider,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  makeStyles,
  shorthands,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwise16Regular,
  Bot20Regular,
  Broom20Regular,
  Open16Regular,
  Person20Regular,
  Send20Regular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { useRunStream } from '../api/sse';
import type { BoardColumnDto, Project, TaskCardDto } from '../api/types';
import {
  CONSOLE_COMMANDS,
  DEFERRED_COMMANDS,
  parseConsoleCommand,
  type ConsoleIntent,
} from './consoleCommands';

// Browser chat control console (Issue #50). A lightweight control-plane REPL:
// it manages projects / backlog / orchestrations through the SAME authorized
// apiClient methods used elsewhere (constitution III — the API is the single
// source of truth; this is a thin client). It NEVER executes agent work itself;
// it only starts/manages existing runs and links OUT to the real gated views.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    minHeight: 0,
    gap: tokens.spacingVerticalM,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  headerText: { display: 'flex', flexDirection: 'column' },
  contextRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  transcript: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    ...shorthands.padding(tokens.spacingVerticalS),
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: tokens.colorNeutralBackground1,
  },
  msgRow: { display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'flex-start' },
  msgRowUser: { flexDirection: 'row-reverse' },
  bubble: {
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalM),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    maxWidth: '80%',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  bubbleUser: { backgroundColor: tokens.colorBrandBackground2 },
  bubbleSystem: { backgroundColor: tokens.colorNeutralBackground3 },
  bubbleError: { backgroundColor: tokens.colorStatusDangerBackground2 },
  bubbleClarify: { backgroundColor: tokens.colorStatusWarningBackground2 },
  bubbleSuccess: { backgroundColor: tokens.colorStatusSuccessBackground2 },
  links: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, marginTop: tokens.spacingVerticalXS },
  linkItem: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXXS },
  link: { color: tokens.colorBrandForegroundLink, textDecorationLine: 'none' },
  composer: { display: 'flex', gap: tokens.spacingHorizontalS },
  input: { flex: 1 },
  monitor: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, ...shorthands.padding(tokens.spacingVerticalM) },
  monitorEvents: {
    maxHeight: '160px',
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  monitorHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalM },
});

interface ConsoleLink {
  label: string;
  to: string;
}

type MsgIntent = 'info' | 'success' | 'error' | 'clarify';

interface ConsoleMessage {
  id: string;
  role: 'user' | 'system';
  intent: MsgIntent;
  text: string;
  links?: ConsoleLink[];
}

interface ExecResult {
  text: string;
  intent: MsgIntent;
  links?: ConsoleLink[];
  // Side effects applied by the caller after the message is appended.
  setActiveProject?: Project | null;
  monitorRunId?: string;
}

let msgSeq = 0;
const nextId = () => `m${Date.now()}-${msgSeq++}`;

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
  const bySubstring = projects.filter((p) => p.name.toLowerCase().includes(q) || p.project_id.toLowerCase().includes(q));
  if (bySubstring.length === 1) return { project: bySubstring[0] };
  if (bySubstring.length > 1) return { candidates: bySubstring };
  return {};
}

function intakeCards(columns: BoardColumnDto[]): TaskCardDto[] {
  return columns
    .filter((c) => c.kind === 'intake')
    .flatMap((c) => c.cards)
    .filter((card): card is TaskCardDto => card.kind === 'task');
}

// A compact live monitor for a run, driven entirely by the shared SSE hook so it
// reuses the exact same streaming + Last-Event-ID resume semantics as the rest
// of the app (acceptance: "reuse apps/web/src/api/sse.ts"; edge case: reconnect).
function RunMonitor({ projectId, runId }: { projectId: string; runId: string }) {
  const styles = useStyles();
  const { events, status, error, reconnect, droppedEventCount } = useRunStream(runId);
  const recent = events.slice(-25);
  return (
    <Card className={styles.monitor}>
      <div className={styles.monitorHeader}>
        <Text weight="semibold">Live monitor · run {runId}</Text>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
          <Badge
            appearance="outline"
            color={status === 'streaming' ? 'success' : status === 'error' ? 'danger' : status === 'done' ? 'informative' : 'warning'}
          >
            {status}
          </Badge>
          <Button size="small" appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={reconnect}>
            Reconnect
          </Button>
          <RouterLink to={`/projects/${projectId}/orchestrations/${runId}`} style={{ display: 'inline-flex' }}>
            <Button size="small" appearance="secondary" icon={<Open16Regular />}>Open run</Button>
          </RouterLink>
        </div>
      </div>
      {error && (
        <MessageBar intent="warning">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {droppedEventCount > 0 && (
        <Caption1>{droppedEventCount} earlier event(s) trimmed from the live buffer — full history is on the run page.</Caption1>
      )}
      <div className={styles.monitorEvents} aria-label="Live run events">
        {recent.length === 0 ? (
          <Caption1>Waiting for events…</Caption1>
        ) : (
          recent.map((e, i) => (
            <Caption1 key={`${e.sequence}-${i}`}>
              <Text weight="semibold">{e.type}</Text>
              {typeof e.payload?.message === 'string' ? ` — ${e.payload.message as string}` : ''}
            </Caption1>
          ))
        )}
      </div>
    </Card>
  );
}

const GREETING: ConsoleMessage = {
  id: 'greeting',
  role: 'system',
  intent: 'info',
  text:
    'Agentweaver control console. This is a thin control-plane REPL — it manages work through the same APIs as the rest of the app and never runs agent work itself. Type `help` to see commands, `projects` to get started.',
};

export function BrowserConsole() {
  const styles = useStyles();
  const [messages, setMessages] = useState<ConsoleMessage[]>([GREETING]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [activeProject, setActiveProject] = useState<Project | null>(null);
  const [monitorRunId, setMonitorRunId] = useState<string>('');
  const transcriptRef = useRef<HTMLDivElement>(null);

  const activeProjectRef = useRef<Project | null>(null);
  useEffect(() => {
    activeProjectRef.current = activeProject;
  }, [activeProject]);

  useEffect(() => {
    const el = transcriptRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, monitorRunId]);

  const append = useCallback((role: ConsoleMessage['role'], intent: MsgIntent, text: string, links?: ConsoleLink[]) => {
    setMessages((prev) => [...prev, { id: nextId(), role, intent, text, links }]);
  }, []);

  const requireProject = (): Project | null => {
    const p = activeProjectRef.current;
    if (!p) {
      return null;
    }
    return p;
  };

  const execute = useCallback(async (intent: ConsoleIntent): Promise<ExecResult> => {
    switch (intent.kind) {
      case 'help': {
        const avail = CONSOLE_COMMANDS.map((c) => `  ${c.usage}\n      ${c.summary}`).join('\n');
        const deferred = DEFERRED_COMMANDS.map((c) => `  ${c.usage} — ${c.summary}`).join('\n');
        return {
          intent: 'info',
          text: `Available commands:\n${avail}\n\nDeferred (use the linked UI — the console never bypasses these gates):\n${deferred}`,
        };
      }
      case 'list_projects': {
        const projects = await apiClient.listProjects();
        if (projects.length === 0) {
          return { intent: 'info', text: 'No projects yet. Create one from the Projects gallery.', links: [{ label: 'Open Projects gallery', to: '/projects' }] };
        }
        const links = projects.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` }));
        return { intent: 'info', text: `${projects.length} project(s). Use \`use <name or id>\` to select one:`, links };
      }
      case 'use_project': {
        const projects = await apiClient.listProjects();
        const { project, candidates } = resolveProject(projects, intent.query);
        if (project) {
          return {
            intent: 'success',
            text: `Active project set to ${project.name} (${project.project_id}).`,
            links: [{ label: `Open ${project.name}`, to: `/projects/${project.project_id}` }],
            setActiveProject: project,
          };
        }
        if (candidates && candidates.length > 1) {
          return {
            intent: 'clarify',
            text: `Multiple projects match "${intent.query}". Which one? Re-run \`use <id>\` with one of:`,
            links: candidates.map((p) => ({ label: `${p.name} (${p.project_id})`, to: `/projects/${p.project_id}` })),
          };
        }
        return { intent: 'error', text: `No project matches "${intent.query}". Run \`projects\` to list them.` };
      }
      case 'list_backlog': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`.' };
        const board = await apiClient.getBoard(p.project_id);
        const cards = intakeCards(board.columns);
        if (cards.length === 0) {
          return { intent: 'info', text: `No backlog/ready items in ${p.name}.`, links: [{ label: 'Open board', to: `/projects/${p.project_id}/board` }] };
        }
        const summary = cards.map((c) => `  [${c.state}] ${c.title} (${c.task_id})`).join('\n');
        return {
          intent: 'info',
          text: `${cards.length} intake item(s) in ${p.name}:\n${summary}`,
          links: [{ label: 'Open board', to: `/projects/${p.project_id}/board` }],
        };
      }
      case 'create_backlog': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`.' };
        const task = await apiClient.captureBacklogTask(p.project_id, { title: intent.title, description: intent.description ?? null });
        return {
          intent: 'success',
          text: `Captured backlog item "${task.title}" (${task.task_id}) in ${p.name}.`,
          links: [{ label: 'View on board', to: `/projects/${p.project_id}/board` }],
        };
      }
      case 'promote_backlog': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`.' };
        const board = await apiClient.getBoard(p.project_id);
        const backlog = intakeCards(board.columns).filter((c) => c.state === 'backlog');
        const q = intent.query.toLowerCase();
        const byId = backlog.find((c) => c.task_id.toLowerCase() === q);
        const matches = byId ? [byId] : backlog.filter((c) => c.title.toLowerCase().includes(q));
        if (matches.length === 0) {
          return { intent: 'error', text: `No backlog item matches "${intent.query}". Run \`backlog\` to see items.` };
        }
        if (matches.length > 1) {
          return {
            intent: 'clarify',
            text: `"${intent.query}" matches ${matches.length} backlog items. Re-run \`ready <task id>\` with one of:\n${matches.map((c) => `  ${c.title} (${c.task_id})`).join('\n')}`,
          };
        }
        const target = matches[0];
        await apiClient.moveTaskToReady(p.project_id, target.task_id);
        return {
          intent: 'success',
          text: `Moved "${target.title}" to Ready in ${p.name}. It will be picked up through the normal heartbeat/pickup flow — no work is started directly by the console.`,
          links: [{ label: 'View on board', to: `/projects/${p.project_id}/board` }],
        };
      }
      case 'list_runs': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`.' };
        const runs = await apiClient.listProjectRuns(p.project_id);
        if (runs.length === 0) {
          return { intent: 'info', text: `No orchestration runs in ${p.name} yet. Start one with \`orchestrate <goal>\`.` };
        }
        const links = runs.slice(0, 25).map((r) => {
          const rid = r.workflow_run_id ?? r.execution_id;
          return { label: `${r.status} · ${r.task.slice(0, 60)} (${rid})`, to: `/projects/${p.project_id}/orchestrations/${rid}` };
        });
        return { intent: 'info', text: `${runs.length} run(s) in ${p.name}:`, links };
      }
      case 'start_orchestration': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`.' };
        const res = await apiClient.startOrchestration(p.project_id, intent.goal);
        return {
          intent: 'success',
          text:
            `Started orchestration in ${p.name}. The coordinator will draft an Outcome plan — you must review and confirm it on the run page before any work is dispatched (the confirmation gate is not bypassed). Run \`monitor ${res.runId}\` to stream updates here.`,
          links: [{ label: 'Open orchestration (confirm Outcome plan)', to: `/projects/${p.project_id}/orchestrations/${res.runId}` }],
          monitorRunId: res.runId,
        };
      }
      case 'monitor': {
        const p = requireProject();
        if (!p) return { intent: 'clarify', text: 'Select a project first with `use <project>`, then `monitor <runId>`.' };
        return {
          intent: 'info',
          text: `Streaming live updates for run ${intent.runId}. Full durable history stays on the run page.`,
          links: [{ label: 'Open run', to: `/projects/${p.project_id}/orchestrations/${intent.runId}` }],
          monitorRunId: intent.runId,
        };
      }
      case 'clarify':
        return { intent: 'clarify', text: intent.message };
      case 'unknown':
        return { intent: 'error', text: `Sorry, I didn't understand "${intent.input}". Type \`help\` for the command list.` };
    }
  }, []);

  const submit = useCallback(async () => {
    const raw = input.trim();
    if (!raw || busy) return;
    append('user', 'info', raw);
    setInput('');
    const intent = parseConsoleCommand(raw);
    setBusy(true);
    try {
      const result = await execute(intent);
      if (result.setActiveProject !== undefined) setActiveProject(result.setActiveProject);
      if (result.monitorRunId) setMonitorRunId(result.monitorRunId);
      append('system', result.intent, result.text, result.links);
    } catch (err) {
      append('system', 'error', errText(err));
    } finally {
      setBusy(false);
    }
  }, [input, busy, append, execute]);

  const bubbleClass = (m: ConsoleMessage) => {
    if (m.role === 'user') return `${styles.bubble} ${styles.bubbleUser}`;
    switch (m.intent) {
      case 'error': return `${styles.bubble} ${styles.bubbleError}`;
      case 'clarify': return `${styles.bubble} ${styles.bubbleClarify}`;
      case 'success': return `${styles.bubble} ${styles.bubbleSuccess}`;
      default: return `${styles.bubble} ${styles.bubbleSystem}`;
    }
  };

  const activeLabel = useMemo(
    () => (activeProject ? `${activeProject.name}` : 'none'),
    [activeProject],
  );

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Bot20Regular />
        <div className={styles.headerText}>
          <Title3>Control console</Title3>
          <Caption1>Conversational control plane · manages work through existing Agentweaver APIs</Caption1>
        </div>
      </div>
      <div className={styles.contextRow}>
        <Text size={200}>Active project:</Text>
        <Badge appearance="tint" color={activeProject ? 'brand' : 'subtle'}>{activeLabel}</Badge>
        <Button
          size="small"
          appearance="subtle"
          icon={<Broom20Regular />}
          onClick={() => { setMessages([GREETING]); setMonitorRunId(''); }}
        >
          Clear
        </Button>
      </div>

      <div className={styles.transcript} ref={transcriptRef} aria-label="Console transcript">
        {messages.map((m) => (
          <div key={m.id} className={`${styles.msgRow} ${m.role === 'user' ? styles.msgRowUser : ''}`}>
            {m.role === 'user' ? <Person20Regular /> : <Bot20Regular />}
            <div className={bubbleClass(m)}>
              <Body1>{m.text}</Body1>
              {m.links && m.links.length > 0 && (
                <div className={styles.links}>
                  {m.links.map((l, i) => (
                    <span key={`${m.id}-l${i}`} className={styles.linkItem}>
                      <Open16Regular />
                      <RouterLink to={l.to} className={styles.link}>{l.label}</RouterLink>
                    </span>
                  ))}
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {monitorRunId && activeProject && (
        <RunMonitor projectId={activeProject.project_id} runId={monitorRunId} />
      )}

      <Divider />
      <div className={styles.composer}>
        <Input
          className={styles.input}
          value={input}
          placeholder="Type a command… e.g. projects, use <name>, add backlog <title>, orchestrate <goal>"
          onChange={(_, d) => setInput(d.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void submit(); } }}
          disabled={busy}
          aria-label="Console command"
        />
        <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <Send20Regular />} disabled={busy || !input.trim()} onClick={() => void submit()}>
          Send
        </Button>
      </div>
    </div>
  );
}
