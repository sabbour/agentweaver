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
import { useProjectRunList } from './runListData';
import { runState } from './runState';
import { OutputCaptureBrowser } from './OutputCaptureBrowser';

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
    minWidth: 0,
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
    minWidth: 0,
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
  form: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minWidth: 0 },
  inline: { display: 'flex', alignItems: 'flex-end', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
});

/** True for a 409 that means the caller saw an old revision. */
function isRevisionConflict(error: unknown): boolean {
  if (!(error instanceof GatewayError) || error.status !== 409) return false;
  const code = errorCode(error);
  // Some 409s are not stale-revision errors: a duplicate link or a reached limit.
  // Rereading would not fix those, so they are shown as normal errors instead.
  return code !== 'priority_link_exists' && !code?.endsWith('_limit_reached');
}

/** Short text for write failures that are not revision conflicts. */
function writeErrorText(error: unknown): string {
  const code = errorCode(error);
  if (code === 'priority_link_exists') return 'This link is already on the Priority.';
  if (code === 'priority_limit_reached') return 'You reached the Priority limit. Drop or remove one first.';
  // Any other limit code is a per-Priority limit, such as too many links or checklist items.
  if (code?.endsWith('_limit_reached')) return 'This Priority reached its limit for that item.';
  if (code === 'project_read_permission_denied') return 'You can no longer read that project.';
  return errorMessage(error);
}

/** Message shown under a Priority or the list: a stale-data conflict, or any other write error. */
type Notice = { kind: 'conflict' } | { kind: 'error'; error: unknown };

/**
 * Renders Priorities. `projects` feeds the run link picker; `conversations`
 * feeds the conversation link picker. Issue and pull request links
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
  // One notice slot per Priority, keyed by priorityId, so an error shows next to the item it belongs to.
  const [notices, setNotices] = useState<Record<string, Notice | undefined>>({});
  // Reorder affects the whole list, so it has its own notice and busy flag.
  const [orderNotice, setOrderNotice] = useState<Notice | null>(null);
  const [orderBusy, setOrderBusy] = useState(false);
  // projectId -> project name, used to label links.
  const projectNames = useMemo(() => new Map((projects ?? []).map((project) => [project.projectId, project.name])), [projects]);
  const list = priorities.list;

  /** Runs one Priority write. Returns true on success; on conflict rereads and keeps the caller's draft. */
  const write = async (
    priorityId: string,
    operation: (token: string, tenant: string | null) => Promise<ChiefOfStaffPriority>,
  ): Promise<boolean> => {
    try {
      // Priority routes use the unbound session (null binding); they are not tied to a run.
      const updated = await apiCall(operation, null);
      // The server returns the whole updated Priority. Swap it into the list in place
      // instead of rereading everything. Order and other items stay as they are.
      priorities.replace((current) => current && {
        ...current,
        items: current.items.map((item) => item.priorityId === updated.priorityId ? updated : item),
      });
      // Success clears any old notice for this Priority.
      setNotices((current) => ({ ...current, [priorityId]: undefined }));
      return true;
    } catch (reason) {
      // A stale revision or a Priority that no longer exists means our copy is out of date.
      // Reread the list and show the conflict notice. We return false so the caller keeps
      // its draft (form text, new item text) and the user can try again.
      if (isRevisionConflict(reason) || (reason instanceof GatewayError && reason.status === 404)) {
        await priorities.reload();
        setNotices((current) => ({ ...current, [priorityId]: { kind: 'conflict' } }));
      } else {
        // Other failures (limits, duplicate link, permission, network) keep the current data.
        setNotices((current) => ({ ...current, [priorityId]: { kind: 'error', error: reason } }));
      }
      return false;
    }
  };

  /** Moves one Priority up (-1) or down (+1) by swapping it with its neighbor. */
  const move = async (index: number, offset: -1 | 1) => {
    if (!list) return;
    const ids = list.items.map((item) => item.priorityId);
    const target = index + offset;
    // Ignore moves past either end of the list.
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    // Disable all move buttons while the reorder is in flight, so two swaps
    // cannot be sent against the same order revision.
    setOrderBusy(true);
    setOrderNotice(null);
    try {
      // Send the full new order plus the order revision we saw. The server
      // rejects it if someone else changed the order in the meantime.
      const next = await apiCall((token, tenant) => gatewayClient.reorderChiefOfStaffPriorities(
        token, { expectedOrderRevision: list.orderRevision, priorityIds: ids }, tenant,
      ), null);
      // The server returns the whole list in its new order.
      priorities.replace(next);
    } catch (reason) {
      // Treat any 409, or an id set that no longer matches the server
      // (a Priority was added or removed elsewhere), as stale order: reread and say so.
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
  // Idempotency key for the current draft. A retry of the same draft reuses it,
  // so a timed-out create that actually succeeded is not created twice.
  const key = useRef<string | null>(null);
  // Ref guard against double submit (for example a fast double Enter). State
  // updates are async, so `busy` alone is not enough.
  const sending = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const value = outcome.trim();
    if (!value || sending.current) return;
    sending.current = true;
    // Create a key only if this draft does not have one yet.
    key.current ??= crypto.randomUUID();
    const idempotencyKey = key.current;
    setBusy(true);
    setError(null);
    try {
      await apiCall((token, tenant) => gatewayClient.createChiefOfStaffPriority(
        token, { outcome: value, notes: notes.trim() || null }, idempotencyKey, tenant,
      ), null);
      // The draft is saved: clear the key so the next draft gets a new one.
      key.current = null;
      setOutcome('');
      setNotes('');
      // Reread so the new Priority appears in its server-assigned position.
      await priorities.reload();
    } catch (reason) {
      // Keep the draft text and key so the user can retry the same create.
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

/** Signature of the panel's shared write helper. Resolves true when the write succeeded. */
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
  // Edit form draft. null means the form is closed. It is local state, so a
  // conflict reread of the list does not overwrite what the user typed.
  const [editing, setEditing] = useState<{ outcome: string; notes: string; state: ChiefOfStaffPriorityState } | null>(null);
  const [newItem, setNewItem] = useState('');
  const [busy, setBusy] = useState(false);
  const id = priority.priorityId;
  const label = priority.outcome;

  // Runs one write for this Priority and disables its controls while it is in flight.
  const run = async (operation: Parameters<WriteFn>[1]) => {
    setBusy(true);
    try {
      return await write(id, operation);
    } finally {
      setBusy(false);
    }
  };

  // The API replaces the whole checklist on each update. Every checklist change
  // (tick, remove, add) sends the full new list with the revision we saw.
  const saveChecklist = (checklist: ChiefOfStaffChecklistItem[], expectedRevision: number) => run((token, tenant) =>
    gatewayClient.updateChiefOfStaffPriority(token, id, { expectedRevision, checklist }, tenant));

  // The current checklist in write shape (only the fields the API accepts).
  // Existing items keep their itemId so the server can match them.
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
          // Capture the draft now; the callback below must not read later edits.
          const draft = editing;
          // Close the form only when the save worked. On conflict it stays open with the draft.
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
        // New items have no itemId; the server assigns one. Clear the input only on success.
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
          {link.access === 'available' && link.projectId && link.runId && link.artifactId && link.kind === 'artifact' && (
            <Button size="small" appearance="secondary" onClick={() => navigate(
              `${runPath(link.projectId!, link.runId!)}?view=changes&captureId=${encodeURIComponent(link.artifactId!)}`,
            )}>Open artifact</Button>
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
  // The caller can no longer read the target. Show only the link kind so no
  // run id, project or number leaks.
  if (link.access === 'unavailable') return (
    <>
      <Badge appearance="tint" color="subtle">Unavailable</Badge>
      <Text>{kind}</Text>
    </>
  );
  // Short text that names the target for each link kind.
  const target = link.kind === 'run' ? `Run ${link.runId ?? ''}`
    : link.kind === 'conversation' ? `Conversation${link.runId ? ` · Run ${link.runId}` : ''}`
      : link.kind === 'artifact' ? `${kind} ${link.artifactId ?? ''}`
        : `${kind} #${link.number ?? ''}`;
  // Prefer the project name; fall back to the id when the project list does not include it.
  const project = link.projectId ? projectNames.get(link.projectId) ?? link.projectId : '';
  // Badge: deleted target -> Not found; run or conversation -> live run state;
  // other kinds -> Unavailable, because the web app cannot read their state.
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

/** Run/conversation choices use unbound reads; capture browsing uses the chosen run's bound token. */
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
  const [kind, setKind] = useState<'run' | 'conversation' | 'artifact'>('run');
  // Only active projects can be linked.
  const activeProjects = useMemo(() => (projects ?? []).filter((project) => project.state === 'active'), [projects]);
  // The picked ids are stored raw. Each effective value below falls back to the
  // first option when the pick is empty or no longer in the list, so the
  // selects never point at a missing option.
  const [pickedProject, setPickedProject] = useState('');
  const projectId = activeProjects.some((project) => project.projectId === pickedProject) ? pickedProject : activeProjects[0]?.projectId ?? '';
  // Read runs only while the form is open and a run list is needed. An empty
  // project id tells the hook to skip the request.
  const runs = useProjectRunList(open && kind !== 'conversation' ? projectId : '');
  const [pickedRun, setPickedRun] = useState('');
  const runId = runs.items.some((item) => item.runId === pickedRun) ? pickedRun : runs.items[0]?.runId ?? '';
  // Only conversations the caller can still read, and that have a project, can be linked.
  const available = conversations.items.filter((item) => item.access === 'available' && item.projectId);
  const [pickedConversation, setPickedConversation] = useState('');
  const conversation = available.find((item) => item.conversationId === pickedConversation) ?? available[0];
  const label = priority.outcome;

  if (!open) return (
    <div className={styles.row}>
      <Button size="small" appearance="secondary" aria-label={`Add link to ${label}`} onClick={() => setOpen(true)}>Add link</Button>
    </div>
  );

  // True when the selected kind has everything it needs to submit.
  const ready = kind === 'run' ? Boolean(projectId && runId) : Boolean(conversation);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    // Artifact links are not submitted by this form. They are added when the
    // user picks a capture in the browser below.
    if (!ready || kind === 'artifact') return;
    // `ready` guarantees `conversation` exists in the conversation branch.
    const body = kind === 'run'
      ? { expectedRevision: priority.revision, kind, projectId, runId }
      : { expectedRevision: priority.revision, kind, projectId: conversation!.projectId!, conversationId: conversation!.conversationId };
    // Close the form only on success; on failure the picks stay for a retry.
    void run((token, tenant) => gatewayClient.addChiefOfStaffPriorityLink(token, priority.priorityId, body, tenant))
      .then((saved) => { if (saved) setOpen(false); });
  };

  return (
    <form className={styles.form} aria-label={`Add link to ${label}`} onSubmit={submit}>
      <div className={styles.inline}>
        <Field label="Link type">
          <Select value={kind} onChange={(_, data) => setKind(data.value as 'run' | 'conversation' | 'artifact')}>
            <option value="run">Run</option>
            <option value="conversation">Conversation</option>
            <option value="artifact">Artifact</option>
          </Select>
        </Field>
        {kind !== 'conversation' && (
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
                    <option key={item.runId} value={item.runId}>{`${item.label} · ${runState(item.executionState).label}`}</option>
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
      {kind !== 'conversation' && activeProjects.length === 0 && <Text className={styles.meta}>No active projects.</Text>}
      {kind !== 'conversation' && projectId && runs.loading && <Text className={styles.meta}>Loading runs…</Text>}
      {kind !== 'conversation' && runs.error != null && <ErrorNotice code={errorCode(runs.error)}>{errorMessage(runs.error)}</ErrorNotice>}
      {kind !== 'conversation' && projectId && !runs.loading && runs.error == null && runs.items.length === 0 && <Text className={styles.meta}>No runs in this project.</Text>}
      {kind !== 'conversation' && runs.loadMore && <Button onClick={runs.loadMore}>Load more runs</Button>}
      {kind === 'conversation' && available.length === 0 && <Text className={styles.meta}>No conversations to link.</Text>}
      {kind === 'artifact' && projectId && runId && <OutputCaptureBrowser
        projectId={projectId} runId={runId} busy={busy}
        onSelect={(artifactId) => void run((token, tenant) => gatewayClient.addChiefOfStaffPriorityLink(
          token, priority.priorityId, { expectedRevision: priority.revision, kind: 'artifact', projectId, runId, artifactId }, tenant,
        )).then((saved) => { if (saved) setOpen(false); })}
      />}
      <div className={styles.row}>
        {kind !== 'artifact' && ready && <Button appearance="primary" type="submit" disabled={busy}>Add link</Button>}
        <Button appearance="secondary" onClick={() => setOpen(false)}>Cancel</Button>
      </div>
    </form>
  );
}