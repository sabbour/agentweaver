/**
 * Chief of Staff chat surfaces: New chat (pick one project, send the first
 * message) and My conversations. A chat is a project run; its history stays on
 * the run page. The browser saves only run identities across the start redirect.
 */
import { Badge, Button, Field, Select, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import {
  readPendingChats,
  runPath,
  savePendingChat,
  useProjectChoices,
  useChiefOfStaffConversations,
  useStartChat,
} from './chiefOfStaff';
import type { ConversationListState, PendingChat } from './chiefOfStaff';
import type { CreateProjectRunRequest, CreateProjectRunResponse, ProjectSummary } from './contracts';
import { errorCode, errorMessage } from './errors';
import { NewChatComposer } from './NewChatComposer';
import { ErrorNotice, Loading, PageHeading } from './pageParts';
import { runState } from './runState';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
  },
  title: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column' },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(110px, auto) minmax(0, 1fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} 0`,
    ':not(:first-child)': { borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  rowMain: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  rowTitle: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  page: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXL },
});

/**
 * New chat: pick one active project, write the first message, send. One
 * idempotency key per draft means a repeated or retried send creates one run.
 * Created runs are saved before start and resumed on mount, so a reload or
 * sign-in redirect continues the same chat.
 */
export function NewChatPanel({ projects, initialProjectId, onChanged }: {
  projects: ProjectSummary[] | null;
  initialProjectId?: string | null;
  /** Called after a run is created or started, so lists can reread. */
  onChanged?: () => void;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const { apiCall, authorize } = useAuth();
  const startChat = useStartChat();
  const choices = useMemo(() => (projects ?? []).filter((project) => project.state === 'active'), [projects]);
  const [picked, setPicked] = useState(initialProjectId ?? '');
  const projectId = choices.some((project) => project.projectId === picked) ? picked : choices[0]?.projectId ?? '';
  const [pending, setPending] = useState<PendingChat | null>(null);
  const [pendingError, setPendingError] = useState<unknown>(null);
  const [starting, setStarting] = useState(false);
  // One request body and one in-flight create per idempotency key: a repeated send reuses both.
  const drafts = useRef(new Map<string, { projectId: string; body: CreateProjectRunRequest }>());
  const inFlight = useRef(new Map<string, Promise<CreateProjectRunResponse>>());

  const runStart = useCallback(async (chat: PendingChat): Promise<void> => {
    const result = await startChat(chat);
    if (result === 'sign_in_required') {
      setPending(chat);
      return;
    }
    setPending(null);
    onChanged?.();
    navigate(runPath(chat.projectId, chat.runId));
  }, [navigate, onChanged, startChat]);

  // Resume once per mount; later renders must not start the same chat again.
  const resumed = useRef(false);
  useEffect(() => {
    if (resumed.current) return undefined;
    const [resume] = readPendingChats();
    if (!resume) return undefined;
    resumed.current = true;
    let active = true;
    let started = false;
    const timer = window.setTimeout(() => {
      started = true;
      setStarting(true);
      void runStart(resume).catch((reason: unknown) => {
        if (!active) return;
        setPending(resume);
        setPendingError(reason);
      }).finally(() => { if (active) setStarting(false); });
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(timer);
      if (!started) resumed.current = false;
    };
  }, [runStart]);

  const submit = async (text: string, key: string) => {
    let draft = drafts.current.get(key);
    if (!draft) {
      const project = choices.find((item) => item.projectId === projectId);
      if (!project) throw new Error('Pick a project.');
      draft = { projectId, body: { firstMessage: text, expectedProjectConfigRevision: project.configurationRevision } };
      drafts.current.set(key, draft);
    }
    const target = draft;
    let create = inFlight.current.get(key);
    if (!create) {
      create = apiCall((token, tenant) => gatewayClient.createProjectRun(token, target.projectId, target.body, key, tenant), null);
      inFlight.current.set(key, create);
      void create.catch(() => inFlight.current.delete(key));
    }
    const created = await create;
    savePendingChat(created);
    setPendingError(null);
    try {
      await runStart({ projectId: created.binding.projectId, runId: created.runId, rootSessionId: created.rootSessionId });
    } catch (reason) {
      // The run exists and stays saved; Retry start continues it without creating another run.
      setPending({ projectId: created.binding.projectId, runId: created.runId, rootSessionId: created.rootSessionId });
      setPendingError(reason);
      onChanged?.();
    }
  };

  const continuePending = async (interactive: boolean) => {
    if (!pending) return;
    setStarting(true);
    setPendingError(null);
    try {
      if (interactive) await authorize({ projectId: pending.projectId, runId: pending.runId });
      await runStart(pending);
    } catch (reason) {
      setPendingError(reason);
    } finally {
      setStarting(false);
    }
  };

  return (
    <section className={styles.card} aria-labelledby="cos-new-chat">
      <Text as="h2" id="cos-new-chat" className={styles.title}>New chat</Text>
      {pending && (
        <div role="status" className={styles.actions}>
          <Text>
            {pendingError
              ? 'The chat was created but did not start.'
              : 'The chat was created. Sign in for this run to start it.'}
          </Text>
          <Button appearance="primary" disabled={starting} onClick={() => void continuePending(!pendingError)}>
            {pendingError ? 'Retry start' : 'Sign in and start'}
          </Button>
        </div>
      )}
      {pendingError != null && <ErrorNotice code={errorCode(pendingError)}>{errorMessage(pendingError)}</ErrorNotice>}
      {projects === null ? <Loading label="Loading projects" /> : choices.length === 0 ? (
        <Text className={styles.muted}>No active projects. Create one in Projects.</Text>
      ) : (
        <NewChatComposer onSubmit={submit} draftScope={projectId} submitLabel="Send" placeholder="What do you need?">
          <Field label="Project">
            <Select value={projectId} onChange={(_, data) => setPicked(data.value)}>
              {choices.map((project) => <option key={project.projectId} value={project.projectId}>{project.name}</option>)}
            </Select>
          </Field>
        </NewChatComposer>
      )}
    </section>
  );
}

/**
 * My conversations: chats the caller started, newest first. Each opens its run
 * page. A chat created but not started offers Start. An unavailable chat shows
 * no project content.
 */
export function ConversationList({ conversations, projectNames }: {
  conversations: ConversationListState;
  projectNames: ReadonlyMap<string, string>;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const startChat = useStartChat();
  const { authorize } = useAuth();
  const [actionError, setActionError] = useState<unknown>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const start = async (chat: PendingChat) => {
    setBusy(chat.runId);
    setActionError(null);
    try {
      if (await startChat(chat) === 'sign_in_required') {
        await authorize({ projectId: chat.projectId, runId: chat.runId });
        if (await startChat(chat) === 'sign_in_required') return;
      }
      navigate(runPath(chat.projectId, chat.runId));
    } catch (reason) {
      setActionError(reason);
      conversations.reload();
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className={styles.card} aria-labelledby="cos-conversations">
      <Text as="h2" id="cos-conversations" className={styles.title}>My conversations</Text>
      {conversations.error != null && <ErrorNotice code={errorCode(conversations.error)}>{errorMessage(conversations.error)}</ErrorNotice>}
      {actionError != null && <ErrorNotice code={errorCode(actionError)}>{errorMessage(actionError)}</ErrorNotice>}
      {conversations.loading ? <Loading label="Loading conversations" /> : conversations.items.length === 0 ? (
        <Text className={styles.muted}>Chats you start appear here.</Text>
      ) : (
        <div className={styles.list} role="list" aria-label="My conversations">
          {conversations.items.map((conversation) => {
            const created = new Date(conversation.createdAt).toLocaleString();
            if (conversation.access === 'unavailable' || !conversation.projectId || !conversation.runId) return (
              <div key={conversation.conversationId} role="listitem" className={styles.row}>
                <Badge appearance="tint" color="subtle">Unavailable</Badge>
                <div className={styles.rowMain}>
                  <Text className={styles.rowTitle}>Unavailable</Text>
                  <Text className={styles.meta}>Started {created}</Text>
                </div>
                <span />
              </div>
            );
            const projectId = conversation.projectId;
            const runId = conversation.runId;
            const awaiting = conversation.requestState === 'awaiting_binding';
            const state = awaiting ? null : runState(conversation.requestState === 'cancelled' ? 'cancelled' : conversation.runState);
            const title = conversation.title || `Run ${runId}`;
            return (
              <div key={conversation.conversationId} role="listitem" className={styles.row}>
                {state ? <Badge appearance="tint" color={state.color}>{state.label}</Badge> : <Badge appearance="outline">Not started</Badge>}
                <div className={styles.rowMain}>
                  <Text className={styles.rowTitle}>{title}</Text>
                  <Text className={styles.meta}>{projectNames.get(projectId) ?? projectId} · Started {created}</Text>
                </div>
                <div className={styles.actions}>
                  {awaiting && conversation.rootSessionId ? (
                    <Button
                      appearance="primary"
                      disabled={busy !== null}
                      aria-label={`Start ${title}`}
                      onClick={() => void start({ projectId, runId, rootSessionId: conversation.rootSessionId! })}
                    >
                      Start
                    </Button>
                  ) : (
                    <Button appearance="secondary" aria-label={`Open ${title}`} onClick={() => navigate(runPath(projectId, runId))}>Open</Button>
                  )}
                </div>
              </div>
            );
          })}
          {conversations.loadMore && <Button appearance="secondary" onClick={conversations.loadMore}>Load more</Button>}
        </div>
      )}
    </section>
  );
}

/** `/assistant`: New chat and My conversations. `?project=` preselects a project. */
export function NewChatPage() {
  const styles = useStyles();
  const [params] = useSearchParams();
  const { projects, error } = useProjectChoices();
  const conversations = useChiefOfStaffConversations();
  const projectNames = useMemo(() => new Map((projects ?? []).map((project) => [project.projectId, project.name])), [projects]);
  return (
    <div className={styles.page}>
      <PageHeading title="New chat" description="Start a chat in one project. Its history stays with the run." breadcrumb={false} />
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      <NewChatPanel projects={projects} initialProjectId={params.get('project')} onChanged={conversations.reload} />
      <ConversationList conversations={conversations} projectNames={projectNames} />
    </div>
  );
}