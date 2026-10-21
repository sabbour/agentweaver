/**
 * Chief of Staff Priorities on Overview: the caller's ordered outcomes with
 * notes, a manual checklist, and typed links. Every write sends the revision the
 * user saw. On a conflict the panel rereads, shows a short notice, and keeps the
 * user's draft. Priority writes only call `/chief-of-staff/priorities` routes;
 * they never call run or gate endpoints.
 */
import {
  Badge,
  Button,
  Checkbox,
  Field,
  Input,
  Select,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useMemo, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import { runPath } from './chiefOfStaff';
import type { ConversationListState, PriorityListState } from './chiefOfStaff';
import type {
  ChiefOfStaffChecklistItem,
  ChiefOfStaffPriority,
  ChiefOfStaffPriorityLink,
  ChiefOfStaffPriorityLinkKind,
  ChiefOfStaffPriorityState,
  ProjectSummary,
} from './contracts';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';
import { useGatewayRunList } from './runListData';
import { runState } from './runState';

/** Shown after a Priority write returns a revision conflict and the panel reread it. */
export const PRIORITY_CONFLICT_NOTICE = 'This Priority changed. Review the current values and try again.';
/** Shown after a reorder returns an order conflict and the panel reread the list. */
export const PRIORITY_ORDER_CONFLICT_NOTICE = 'The order changed. Review the current order and try again.';

const STATE_LABELS: Record<ChiefOfStaffPriorityState, string> = { active: 'Active', achieved: 'Achieved', dropped: 'Dropped' };
const STATE_COLORS: Record<ChiefOfStaffPriorityState, 'brand' | 'success' | 'subtle'> = { active: 'brand', achieved: 'success', dropped: 'subtle' };
const KIND_LABELS: Record<ChiefOfStaffPriorityLinkKind, string> = {
  run: 'Run', conversation: 'Conversation', issue: 'Issue', pull_request: 'Pull request', artifact: 'Artifact',
};

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
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, margin: 0, padding: 0, listStyleType: 'none' },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  head: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  outcome: { fontWeight: tokens.fontWeightSemibold, flexGrow: 1, minWidth: 0 },
  row: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  sub: { fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold },
  meta: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  inline: { display: 'flex', alignItems: 'flex-end', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
});

/** True for a 409 that means the caller saw an old revision. */
function isRevisionConflict(error: unknown): boolean {
  if (!(error instanceof GatewayError) || error.status !== 409) return false;
  const code = errorCode(error);
  return code !== 'priority_link_exists' && !code?.endsWith('_limit_reached');
}

/** Short text for write failures that are not revision conflicts. */
function writeErrorText(error: unknown): string {
  const code = errorCode(error);
  if (code === 'priority_link_exists') return 'This link is already on the Priority.';
  if (code === 'priority_limit_reached') return 'You reached the Priority limit. Drop or remove one first.';
  if (code?.endsWith('_limit_reached')) return 'This Priority reached its limit for that item.';
  if (code === 'project_read_permission_denied') return 'You can no longer read that project.';
  return errorMessage(error);
}

type Notice = { kind: 'conflict' } | { kind: 'error'; error: unknown };

/**
 * Renders Priorities. `projects` feeds the run link picker; `conversations`
 * feeds the conversation link picker. Issue, pull request, and artifact links
 * are shown when present but not offered, because the web app has no picker
 * source for them.
 */
export function PrioritiesPanel({ priorities, projects, conversations }: {
  priorities: PriorityListState;
  projects: ProjectSummary[] | null;
  conversations: ConversationListState;
}) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const [notices, setNotices] = useState<Record<string, Notice | undefined>>({});
  const [orderNotice, setOrderNotice] = useState<Notice | null>(null);
  const [orderBusy, setOrderBusy] = useState(false);
  const projectNames = useMemo(() => new Map((projects ?? []).map((project) => [project.projectId, project.name])), [projects]);
  const list = priorities.list;

  /** Runs one Priority write. Returns true on success; on conflict rereads and keeps the caller's draft. */
  const write = async (
    priorityId: string,
    operation: (token: string, tenant: string | null) => Promise<ChiefOfStaffPriority>,
  ): Promise<boolean> => {
    try {
      const updated = await apiCall(operation, null);
      priorities.replace((current) => current && {
        ...current,
        items: current.items.map((item) => item.priorityId === updated.priorityId ? updated : item),
      });
      setNotices((current) => ({ ...current, [priorityId]: undefined }));
      return true;
    } catch (reason) {
      if (isRevisionConflict(reason) || (reason instanceof GatewayError && reason.status === 404)) {
        await priorities.reload();
        setNotices((current) => ({ ...current, [priorityId]: { kind: 'conflict' } }));
      } else {
        setNotices((current) => ({ ...current, [priorityId]: { kind: 'error', error: reason } }));
      }
      return false;
    }
  };

  const move = async (index: number, offset: -1 | 1) => {
    if (!list) return;
    const ids = list.items.map((item) => item.priorityId);
    const target = index + offset;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    setOrderBusy(true);
    setOrderNotice(null);
    try {
      const next = await apiCall((token, tenant) => gatewayClient.reorderChiefOfStaffPriorities(
        token, { expectedOrderRevision: list.orderRevision, priorityIds: ids }, tenant,
      ), null);
      priorities.replace(next);
    } catch (reason) {
      const stale = (reason instanceof GatewayError && reason.status === 409) || errorCode(reason) === 'priority_order_invalid';
      if (stale) await priorities.reload();
      setOrderNotice(stale ? { kind: 'conflict' } : { kind: 'error', error: reason });
    } finally {
      setOrderBusy(false);
    }
  };

  return (
    <section className={styles.card} aria-labelledby="cos-priorities">
      <Text as="h2" id="cos-priorities" className={styles.title}>Priorities</Text>
      {priorities.error != null && <ErrorNotice code={errorCode(priorities.error)}>{errorMessage(priorities.error)}</ErrorNotice>}
      {orderNotice && (
        <Text role="alert">{orderNotice.kind === 'conflict' ? PRIORITY_ORDER_CONFLICT_NOTICE : writeErrorText(orderNotice.error)}</Text>
      )}
      {priorities.loading && !list ? <Loading label="Loading Priorities" /> : list && (
        list.items.length === 0 ? <Text className={styles.muted}>No Priorities yet. Add the outcomes you are working toward.</Text> : (
          <ol className={styles.list} aria-label="Priorities">
            {list.items.map((priority, index) => (
              <PriorityItem
                key={priority.priorityId}
                priority={priority}
                index={index}
                count={list.items.length}
                notice={notices[priority.priorityId]}
                orderBusy={orderBusy}
                projects={projects}
                projectNames={projectNames}
                conversations={conversations}
                write={write}
                move={move}
              />
            ))}
          </ol>
        )
      )}
      <CreatePriorityForm priorities={priorities} />
    </section>
  );
}

/** New Priority form. One idempotency key per draft; changing the text starts a new draft. */
function CreatePriorityForm({ priorities }: { priorities: PriorityListState }) {
  const styles = useStyles();
  const { apiCall } = useAuth();
  const [outcome, setOutcome] = useState('');
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const key = useRef<string | null>(null);
  const sending = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const value = outcome.trim();
    if (!value || sending.current) return;
    sending.current = true;
    key.current ??= crypto.randomUUID();
    const idempotencyKey = key.current;
    setBusy(true);
    setError(null);
    try {
      await apiCall((token, tenant) => gatewayClient.createChiefOfStaffPriority(
        token, { outcome: value, notes: notes.trim() || null }, idempotencyKey, tenant,
      ), null);
      key.current = null;
      setOutcome('');
      setNotes('');
      await priorities.reload();
    } catch (reason) {
      setError(reason);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  };

  return (
    <form className={styles.form} aria-label="Add a Priority" onSubmit={(event) => void submit(event)}>
      <Text as="h3" className={styles.sub}>Add a Priority</Text>
      {error != null && <ErrorNotice code={errorCode(error)}>{writeErrorText(error)}</ErrorNotice>}
      <Field label="Outcome">
        <Input value={outcome} onChange={(_, data) => { setOutcome(data.value); key.current = null; }} />
      </Field>
      <Field label="Notes">
        <Textarea value={notes} rows={2} onChange={(_, data) => { setNotes(data.value); key.current = null; }} />
      </Field>
      <div className={styles.row}>
        <Button appearance="primary" type="submit" disabled={busy || !outcome.trim()}>{busy ? 'Adding…' : 'Add Priority'}</Button>
      </div>
    </form>
  );
}

type WriteFn = (
  priorityId: string,
  operation: (token: string, tenant: string | null) => Promise<ChiefOfStaffPriority>,
) => Promise<boolean>;

/** One Priority with its edit form, checklist, and links. Drafts survive a conflict reread. */
function PriorityItem({ priority, index, count, notice, orderBusy, projects, projectNames, conversations, write, move }: {
  priority: ChiefOfStaffPriority;
  index: number;
  count: number;
  notice: Notice | undefined;
  orderBusy: boolean;
  projects: ProjectSummary[] | null;
  projectNames: ReadonlyMap<string, string>;
  conversations: ConversationListState;
  write: WriteFn;
  move: (index: number, offset: -1 | 1) => Promise<void>;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [editing, setEditing] = useState<{ outcome: string; notes: string; state: ChiefOfStaffPriorityState } | null>(null);
  const [newItem, setNewItem] = useState('');
  const [busy, setBusy] = useState(false);
  const id = priority.priorityId;
  const label = priority.outcome;

  const run = async (operation: Parameters<WriteFn>[1]) => {
    setBusy(true);
    try {
      return await write(id, operation);
    } finally {
      setBusy(false);
    }
  };

  const saveChecklist = (checklist: ChiefOfStaffChecklistItem[], expectedRevision: number) => run((token, tenant) =>
    gatewayClient.updateChiefOfStaffPriority(token, id, { expectedRevision, checklist }, tenant));

  const current = priority.checklist.map((item) => ({ itemId: item.itemId, text: item.text, done: item.done }));

  return (
    <li className={styles.item} aria-label={`Priority ${index + 1}: ${label}`}>
      <div className={styles.head}>
        <Badge appearance="tint" color={STATE_COLORS[priority.state]}>{STATE_LABELS[priority.state]}</Badge>
        <Text className={styles.outcome}>{priority.outcome}</Text>
        {index > 0 && (
          <Button size="small" appearance="subtle" disabled={orderBusy} aria-label={`Move up: ${label}`} onClick={() => void move(index, -1)}>Move up</Button>
        )}
        {index < count - 1 && (
          <Button size="small" appearance="subtle" disabled={orderBusy} aria-label={`Move down: ${label}`} onClick={() => void move(index, 1)}>Move down</Button>
        )}
        {!editing && (
          <Button size="small" appearance="secondary" aria-label={`Edit: ${label}`} onClick={() => setEditing({
            outcome: priority.outcome, notes: priority.notes ?? '', state: priority.state,
          })}>Edit</Button>
        )}
      </div>
      {priority.notes && !editing && <Text>{priority.notes}</Text>}
      {notice && (
        <Text role="alert">{notice.kind === 'conflict' ? PRIORITY_CONFLICT_NOTICE : writeErrorText(notice.error)}</Text>
      )}
      {editing && (
        <form className={styles.form} aria-label={`Edit Priority: ${label}`} onSubmit={(event) => {
          event.preventDefault();
          const draft = editing;
          void run((token, tenant) => gatewayClient.updateChiefOfStaffPriority(token, id, {
            expectedRevision: priority.revision,
            outcome: draft.outcome.trim(),
            notes: draft.notes.trim() || null,
            state: draft.state,
          }, tenant)).then((saved) => { if (saved) setEditing(null); });
        }}>
          <Field label="Outcome">
            <Input value={editing.outcome} onChange={(_, data) => setEditing({ ...editing, outcome: data.value })} />
          </Field>
          <Field label="Notes">
            <Textarea value={editing.notes} rows={2} onChange={(_, data) => setEditing({ ...editing, notes: data.value })} />
          </Field>
          <Field label="State">
            <Select value={editing.state} onChange={(_, data) => setEditing({ ...editing, state: data.value as ChiefOfStaffPriorityState })}>
              <option value="active">Active</option>
              <option value="achieved">Achieved</option>
              <option value="dropped">Dropped</option>
            </Select>
          </Field>
          <div className={styles.row}>
            <Button appearance="primary" type="submit" disabled={busy || !editing.outcome.trim()}>Save</Button>
            <Button appearance="secondary" onClick={() => setEditing(null)}>Cancel</Button>
          </div>
        </form>
      )}
      <Text as="h3" className={styles.sub}>Checklist</Text>
      {priority.checklist.length === 0 && <Text className={styles.meta}>No checklist items.</Text>}
      {priority.checklist.map((item) => (
        <div key={item.itemId} className={styles.row}>
          <Checkbox
            label={item.text}
            checked={item.done}
            disabled={busy}
            onChange={(_, data) => void saveChecklist(
              current.map((entry) => entry.itemId === item.itemId ? { ...entry, done: data.checked === true } : entry),
              priority.revision,
            )}
          />
          <Button size="small" appearance="subtle" disabled={busy} aria-label={`Remove checklist item: ${item.text}`} onClick={() => void saveChecklist(
            current.filter((entry) => entry.itemId !== item.itemId),
            priority.revision,
          )}>Remove</Button>
        </div>
      ))}
      <form className={styles.inline} aria-label={`Add checklist item to ${label}`} onSubmit={(event) => {
        event.preventDefault();
        const text = newItem.trim();
        if (!text) return;
        void saveChecklist([...current, { text, done: false }], priority.revision).then((saved) => { if (saved) setNewItem(''); });
      }}>
        <Field label="New checklist item">
          <Input value={newItem} onChange={(_, data) => setNewItem(data.value)} />
        </Field>
        <Button type="submit" appearance="secondary" disabled={busy || !newItem.trim()}>Add item</Button>
      </form>
      <Text as="h3" className={styles.sub}>Links</Text>
      {priority.links.length === 0 && <Text className={styles.meta}>No links.</Text>}
      {priority.links.map((link) => (
        <div key={link.linkId} className={styles.row}>
          <LinkSummary link={link} projectNames={projectNames} />
          {link.access === 'available' && link.projectId && link.runId && (link.kind === 'run' || link.kind === 'conversation') && (
            <Button size="small" appearance="secondary" onClick={() => navigate(runPath(link.projectId!, link.runId!))}>Open</Button>
          )}
          <Button size="small" appearance="subtle" disabled={busy} aria-label={`Remove link: ${KIND_LABELS[link.kind]}`} onClick={() => void run((token, tenant) =>
            gatewayClient.removeChiefOfStaffPriorityLink(token, id, link.linkId, priority.revision, tenant))}>Remove</Button>
        </div>
      ))}
      <AddLinkForm
        priority={priority}
        busy={busy}
        projects={projects}
        projectNames={projectNames}
        conversations={conversations}
        run={run}
      />
    </li>
  );
}

/**
 * One link's state. Available run and conversation links show the run state;
 * issue, pull request, and artifact links show Unavailable because the web app
 * has no source for their state. Unavailable links show only the kind; missing
 * targets show Not found.
 */
export function LinkSummary({ link, projectNames }: { link: ChiefOfStaffPriorityLink; projectNames: ReadonlyMap<string, string> }) {
  const styles = useStyles();
  const kind = KIND_LABELS[link.kind];
  if (link.access === 'unavailable') return (
    <>
      <Badge appearance="tint" color="subtle">Unavailable</Badge>
      <Text>{kind}</Text>
    </>
  );
  const target = link.kind === 'run' ? `Run ${link.runId ?? ''}`
    : link.kind === 'conversation' ? `Conversation${link.runId ? ` · Run ${link.runId}` : ''}`
      : link.kind === 'artifact' ? `${kind} ${link.artifactId ?? ''}`
        : `${kind} #${link.number ?? ''}`;
  const project = link.projectId ? projectNames.get(link.projectId) ?? link.projectId : '';
  const badge = link.access === 'missing'
    ? { label: 'Not found', color: 'subtle' as const }
    : link.kind === 'run' || link.kind === 'conversation'
      ? runState(link.state)
      : { label: 'Unavailable', color: 'subtle' as const };
  return (
    <>
      <Badge appearance="tint" color={badge.color}>{badge.label}</Badge>
      <Text>{target.trim()}</Text>
      {project && <Text className={styles.meta}>{project}</Text>}
    </>
  );
}

/** Adds a run or conversation link through pickers. No identifier typing. */
function AddLinkForm({ priority, busy, projects, projectNames, conversations, run }: {
  priority: ChiefOfStaffPriority;
  busy: boolean;
  projects: ProjectSummary[] | null;
  projectNames: ReadonlyMap<string, string>;
  conversations: ConversationListState;
  run: (operation: Parameters<WriteFn>[1]) => Promise<boolean>;
}) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [kind, setKind] = useState<'run' | 'conversation'>('run');
  const activeProjects = useMemo(() => (projects ?? []).filter((project) => project.state === 'active'), [projects]);
  const [pickedProject, setPickedProject] = useState('');
  const projectId = activeProjects.some((project) => project.projectId === pickedProject) ? pickedProject : activeProjects[0]?.projectId ?? '';
  const runs = useGatewayRunList(open && kind === 'run' ? projectId : '');
  const [pickedRun, setPickedRun] = useState('');
  const runId = runs.items.some((item) => item.runId === pickedRun) ? pickedRun : runs.items[0]?.runId ?? '';
  const available = conversations.items.filter((item) => item.access === 'available' && item.projectId);
  const [pickedConversation, setPickedConversation] = useState('');
  const conversation = available.find((item) => item.conversationId === pickedConversation) ?? available[0];
  const label = priority.outcome;

  if (!open) return (
    <div className={styles.row}>
      <Button size="small" appearance="secondary" aria-label={`Add link to ${label}`} onClick={() => setOpen(true)}>Add link</Button>
    </div>
  );

  const ready = kind === 'run' ? Boolean(projectId && runId) : Boolean(conversation);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!ready) return;
    const body = kind === 'run'
      ? { expectedRevision: priority.revision, kind, projectId, runId }
      : { expectedRevision: priority.revision, kind, projectId: conversation!.projectId!, conversationId: conversation!.conversationId };
    void run((token, tenant) => gatewayClient.addChiefOfStaffPriorityLink(token, priority.priorityId, body, tenant))
      .then((saved) => { if (saved) setOpen(false); });
  };

  return (
    <form className={styles.form} aria-label={`Add link to ${label}`} onSubmit={submit}>
      <div className={styles.inline}>
        <Field label="Link type">
          <Select value={kind} onChange={(_, data) => setKind(data.value as 'run' | 'conversation')}>
            <option value="run">Run</option>
            <option value="conversation">Conversation</option>
          </Select>
        </Field>
        {kind === 'run' && (
          <>
            <Field label="Project">
              <Select value={projectId} onChange={(_, data) => setPickedProject(data.value)}>
                {activeProjects.map((project) => <option key={project.projectId} value={project.projectId}>{project.name}</option>)}
              </Select>
            </Field>
            {runs.items.length > 0 && (
              <Field label="Run">
                <Select value={runId} onChange={(_, data) => setPickedRun(data.value)}>
                  {runs.items.map((item) => (
                    <option key={item.runId} value={item.runId}>{`${item.title || `Run ${item.runId}`} · ${runState(item.executionState).label}`}</option>
                  ))}
                </Select>
              </Field>
            )}
          </>
        )}
        {kind === 'conversation' && available.length > 0 && (
          <Field label="Conversation">
            <Select value={conversation?.conversationId ?? ''} onChange={(_, data) => setPickedConversation(data.value)}>
              {available.map((item) => (
                <option key={item.conversationId} value={item.conversationId}>
                  {`${item.title || `Run ${item.runId}`} · ${projectNames.get(item.projectId!) ?? item.projectId}`}
                </option>
              ))}
            </Select>
          </Field>
        )}
      </div>
      {kind === 'run' && activeProjects.length === 0 && <Text className={styles.meta}>No active projects.</Text>}
      {kind === 'run' && projectId && runs.loading && <Text className={styles.meta}>Loading runs…</Text>}
      {kind === 'run' && runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{errorMessage(runs.error)}</ErrorNotice>}
      {kind === 'run' && projectId && !runs.loading && runs.error == null && runs.items.length === 0 && <Text className={styles.meta}>No runs in this project.</Text>}
      {kind === 'conversation' && available.length === 0 && <Text className={styles.meta}>No conversations to link.</Text>}
      <div className={styles.row}>
        {ready && <Button appearance="primary" type="submit" disabled={busy}>Add link</Button>}
        <Button appearance="secondary" onClick={() => setOpen(false)}>Cancel</Button>
      </div>
    </form>
  );
}