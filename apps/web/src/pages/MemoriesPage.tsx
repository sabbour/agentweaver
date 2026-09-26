import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Tab,
  TabList,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
} from '../components/ui';
import { Pager } from '../copilot-fluent-system';
import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { AgentMemoryDto, AgentMemoryRevisionDto, DecisionDto, DecisionInboxEntryDto, DecisionRevisionDto, PagedResult, SessionHistoryDto } from '../api/types';

const useStyles = makeStyles({
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  breadcrumbSep: {
    color: tokens.colorNeutralForeground4,
  },
  tabContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  itemList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  itemHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  itemTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    flexGrow: 1,
  },
  itemMeta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
  itemContent: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    lineHeight: '1.6',
    whiteSpace: 'pre-wrap',
  },
  itemRationale: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    fontStyle: 'italic',
    lineHeight: '1.5',
  },
  proposedItem: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    marginTop: tokens.spacingVerticalXS,
  },
  form: {
    display: 'grid',
    gap: tokens.spacingVerticalS,
    maxWidth: '720px',
  },
  inlineFields: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
});

function formatApiError(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body || 'Request failed'}`;
  return err instanceof Error ? err.message : String(err);
}

function parseActiveIssues(value?: string | null): string[] {
  if (!value) return [];
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : [];
  } catch {
    return value.split('\n').map((item) => item.trim()).filter(Boolean);
  }
}

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState<'decisions' | 'memory' | 'sessions'>('decisions');
  const [decisions,   setDecisions]   = useState<DecisionDto[] | null>(null);
  const [decisionsTotalCount, setDecisionsTotalCount] = useState(0);
  const [decisionsPage, setDecisionsPage] = useState(1);
  const [decisionsPageSize, setDecisionsPageSize] = useState(25);
  const [inbox,       setInbox]       = useState<DecisionInboxEntryDto[] | null>(null);
  const [inboxTotalCount, setInboxTotalCount] = useState(0);
  const [inboxPage, setInboxPage] = useState(1);
  const [inboxPageSize, setInboxPageSize] = useState(25);
  const [memory,      setMemory]      = useState<AgentMemoryDto[] | null>(null);
  const [memoryTotalCount, setMemoryTotalCount] = useState(0);
  const [memoryPage, setMemoryPage] = useState(1);
  const [memoryPageSize, setMemoryPageSize] = useState(25);
  const [sessions, setSessions] = useState<SessionHistoryDto[] | null>(null);
  const [sessionsTotalCount, setSessionsTotalCount] = useState(0);
  const [sessionsPage, setSessionsPage] = useState(1);
  const [sessionsPageSize, setSessionsPageSize] = useState(25);
  const [loading,     setLoading]     = useState(false);
  const [loadError,   setLoadError]   = useState<string | null>(null);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [newAgentName, setNewAgentName] = useState('Coordinator');
  const [newType, setNewType] = useState('learning');
  const [newContent, setNewContent] = useState('');
  const [editingMemoryId, setEditingMemoryId] = useState<string | null>(null);
  const [editType, setEditType] = useState('');
  const [editContent, setEditContent] = useState('');
  const [memoryQuery, setMemoryQuery] = useState('');
  const [appliedMemoryQuery, setAppliedMemoryQuery] = useState('');
  const [memoryStatus, setMemoryStatus] = useState('active');
  const [memoryHistory, setMemoryHistory] = useState<Record<string, PagedResult<AgentMemoryRevisionDto>>>({});
  const [decisionHistory, setDecisionHistory] = useState<Record<string, PagedResult<DecisionRevisionDto>>>({});

  const loadDecisionsPage = useCallback(async () => {
    if (!projectId) return;
    const [d, initialInbox] = await Promise.all([
      apiClient.getDecisions(projectId, { page: decisionsPage, pageSize: decisionsPageSize }),
      apiClient.getDecisionsInbox(projectId, { page: inboxPage, pageSize: inboxPageSize }),
    ]);

    let nextInbox = initialInbox;
    if (initialInbox.items.length === 0 && inboxPage > 1) {
      const lastValidPage = Math.max(1, Math.ceil(initialInbox.total_count / Math.max(1, inboxPageSize)));
      if (lastValidPage !== inboxPage) {
        nextInbox = await apiClient.getDecisionsInbox(projectId, { page: lastValidPage, pageSize: inboxPageSize });
        setInboxPage(lastValidPage);
      }
    }

    setDecisions(d.items);
    setDecisionsTotalCount(d.total_count);
    setInbox(nextInbox.items);
    setInboxTotalCount(nextInbox.total_count);
  }, [decisionsPage, decisionsPageSize, inboxPage, inboxPageSize, projectId]);

  useEffect(() => {
    if (!projectId) return;
    const loadTabData = async () => {
      setLoading(true);
      setLoadError(null);

      if (selectedTab === 'decisions') {
        if (decisions !== null && inbox !== null) { setLoading(false); return; }
        try {
          await loadDecisionsPage();
        } catch (err: unknown) {
          setDecisions([]);
          setDecisionsTotalCount(0);
          setInbox([]);
          setInboxTotalCount(0);
          setLoadError(formatApiError(err));
        } finally {
          setLoading(false);
        }
        return;
      }
      if (selectedTab === 'memory') {
        if (memory !== null) { setLoading(false); return; }
        try {
          const m = await apiClient.getProjectMemory(projectId, {
            page: memoryPage,
            pageSize: memoryPageSize,
            query: appliedMemoryQuery,
            status: memoryStatus,
          });
          setMemory(m.items);
          setMemoryTotalCount(m.total_count);
        } catch (err: unknown) {
          setMemory([]);
          setMemoryTotalCount(0);
          setLoadError(formatApiError(err));
        } finally {
          setLoading(false);
        }
        return;
      }
      if (sessions !== null) { setLoading(false); return; }
      try {
        const result = await apiClient.getProjectSessions(projectId, { page: sessionsPage, pageSize: sessionsPageSize });
        setSessions(result.items);
        setSessionsTotalCount(result.total_count);
      } catch (err: unknown) {
        setSessions([]);
        setSessionsTotalCount(0);
        setLoadError(formatApiError(err));
      } finally {
        setLoading(false);
      }
    };
    void loadTabData();
  }, [projectId, selectedTab, decisions, inbox, memory, sessions, reloadKey, loadDecisionsPage, memoryPage, memoryPageSize, sessionsPage, sessionsPageSize, appliedMemoryQuery, memoryStatus]);

  const retryLoad = () => {
    if (selectedTab === 'decisions') {
      setDecisions(null);
      setInbox(null);
    } else if (selectedTab === 'memory') {
      setMemory(null);
    } else {
      setSessions(null);
    }
    setLoadError(null);
    setReloadKey((key) => key + 1);
  };

  const refreshDecisions = () => {
    setDecisions(null);
    setInbox(null);
    setReloadKey((key) => key + 1);
  };

  const refreshMemory = () => {
    setMemory(null);
    setReloadKey((key) => key + 1);
  };

  const runInboxAction = async (entryId: string, action: 'merge' | 'promote' | 'reject') => {
    if (!projectId || busyAction) return;
    setBusyAction(`${action}:${entryId}`);
    setMutationError(null);
    try {
      if (action === 'merge') await apiClient.mergeDecisionInboxEntry(projectId, entryId);
      else if (action === 'promote') await apiClient.promoteDecisionInboxEntry(projectId, entryId);
      else await apiClient.rejectDecisionInboxEntry(projectId, entryId);
      refreshDecisions();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const createMemory = async () => {
    if (!projectId || !newAgentName.trim() || !newType.trim() || !newContent.trim()) return;
    setBusyAction('create-memory');
    setMutationError(null);
    try {
      await apiClient.createAgentMemory(projectId, newAgentName.trim(), { type: newType.trim(), content: newContent.trim() });
      setNewContent('');
      refreshMemory();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const beginEditMemory = (entry: AgentMemoryDto) => {
    setEditingMemoryId(entry.id);
    setEditType(entry.type);
    setEditContent(entry.content);
  };

  const updateMemory = async (entry: AgentMemoryDto) => {
    if (!projectId || !editType.trim() || !editContent.trim()) return;
    setBusyAction(`update-memory:${entry.id}`);
    setMutationError(null);
    try {
      await apiClient.updateAgentMemory(projectId, entry.agent_name, entry.id, {
        expected_revision: entry.revision,
        type: editType.trim(),
        content: editContent.trim(),
        reason: 'Updated from the Memories page',
      });
      setEditingMemoryId(null);
      refreshMemory();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const loadMemoryHistory = async (entry: AgentMemoryDto, page = 1) => {
    if (!projectId || busyAction) return;
    if (page === 1 && memoryHistory[entry.id]) {
      setMemoryHistory((current) => {
        const next = { ...current };
        delete next[entry.id];
        return next;
      });
      return;
    }
    setBusyAction(`history:${entry.id}`);
    setMutationError(null);
    try {
      const result = await apiClient.getAgentMemoryRevisions(
        projectId, entry.agent_name, entry.id, { page, pageSize: 25 });
      setMemoryHistory((current) => ({
        ...current,
        [entry.id]: page === 1
          ? result
          : { ...result, items: [...(current[entry.id]?.items ?? []), ...result.items] },
      }));
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const restoreMemory = async (entry: AgentMemoryDto, revision: number) => {
    if (!projectId || busyAction) return;
    setBusyAction(`restore:${entry.id}:${revision}`);
    setMutationError(null);
    try {
      await apiClient.restoreAgentMemory(projectId, entry.agent_name, entry.id, entry.revision, revision);
      setMemoryHistory((current) => {
        const next = { ...current };
        delete next[entry.id];
        return next;
      });
      refreshMemory();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const loadDecisionHistory = async (entry: DecisionDto, page = 1) => {
    if (!projectId || busyAction) return;
    if (page === 1 && decisionHistory[entry.id]) {
      setDecisionHistory((current) => {
        const next = { ...current };
        delete next[entry.id];
        return next;
      });
      return;
    }
    setBusyAction(`decision-history:${entry.id}`);
    setMutationError(null);
    try {
      const result = await apiClient.getDecisionRevisions(projectId, entry.id, { page, pageSize: 25 });
      setDecisionHistory((current) => ({
        ...current,
        [entry.id]: page === 1
          ? result
          : { ...result, items: [...(current[entry.id]?.items ?? []), ...result.items] },
      }));
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  const restoreDecision = async (entry: DecisionDto, revision: number) => {
    if (!projectId || busyAction) return;
    setBusyAction(`decision-restore:${entry.id}:${revision}`);
    setMutationError(null);
    try {
      await apiClient.restoreDecision(projectId, entry.id, entry.revision, revision);
      setDecisionHistory((current) => {
        const next = { ...current };
        delete next[entry.id];
        return next;
      });
      refreshDecisions();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusyAction(null);
    }
  };

  // The `/decisions/inbox` endpoint defaults to `status=pending` server-side, so `inbox` is
  // already just the pending page — this filter is a defensive no-op guarding against a future
  // caller passing an explicit `status` param.
  const pending = (inbox ?? []).filter(e => e.status === 'pending');
  const hasActiveDecisions = decisions !== null && decisions.length > 0;
  const busy = busyAction !== null;
  const decisionCount = decisionsTotalCount;
  const memoryCount = memoryTotalCount;
  const sessionCount = sessionsTotalCount;

  return (
    <PageContainer>
      <PageHeader
        title="Team memory"
        description="Decisions and learnings the team has captured."
        breadcrumbs={
          <>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <span>Team memory</span>
          </>
        }
      />

      <MetricRow items={[
        { label: 'Pending', value: inboxTotalCount },
        { label: 'Decisions', value: decisionCount },
        { label: 'Memories', value: memoryCount },
        { label: 'Sessions', value: sessionCount },
      ]} />

      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_, data) => setSelectedTab(data.value as 'decisions' | 'memory' | 'sessions')}
      >
        <Tab value="decisions">Decisions</Tab>
        <Tab value="memory">Agent memory</Tab>
        <Tab value="sessions">Session history</Tab>
      </TabList>

      <div className={styles.tabContent}>
        {loading && <LoadingState rows={3} />}
        {loadError && <ErrorState message={loadError} onRetry={retryLoad} />}
        {mutationError && (
          <MessageBar intent="error">
            <MessageBarBody>{mutationError}</MessageBarBody>
          </MessageBar>
        )}

        {!loading && !loadError && selectedTab === 'decisions' && (
          !hasActiveDecisions && inboxTotalCount === 0
            ? (
              <EmptyState
                title="No decisions recorded yet"
                description="Accepted decisions and pending proposals will appear here."
              />
            )
            : (
              <>
                {hasActiveDecisions && (
                  <PageSection title="Accepted decisions">
                    <div className={styles.itemList}>
                      {decisions!.map(d => (
                        <div key={d.id} className={styles.item}>
                          <div className={styles.itemHeader}>
                            <span className={styles.itemTitle}>{d.title}</span>
                            <Badge appearance="tint" color="subtle">{d.type}</Badge>
                            <Badge appearance="outline">{d.agent_name}</Badge>
                            <span className={styles.itemMeta}>Revision {d.revision}</span>
                            <span className={styles.itemMeta}>{new Date(d.created_at).toLocaleString()}</span>
                          </div>
                          <span className={styles.itemContent}>{d.content}</span>
                          {d.rationale && (
                            <span className={styles.itemRationale}>Rationale: {d.rationale}</span>
                          )}
                          <div className={styles.actions}>
                            <Button size="small" disabled={busy} onClick={() => void loadDecisionHistory(d)}>
                              {decisionHistory[d.id] ? 'Hide history' : 'History'}
                            </Button>
                          </div>
                          {decisionHistory[d.id] && (
                            <div className={styles.itemList}>
                              {decisionHistory[d.id].items.map((revision, index, revisions) => (
                                <div key={revision.revision_id} className={styles.proposedItem}>
                                  <div className={styles.itemHeader}>
                                    <strong>Revision {revision.revision}</strong>
                                    <Badge appearance="outline">{revision.status}</Badge>
                                    <span className={styles.itemMeta}>
                                      {revision.reason} by {revision.actor} · {new Date(revision.created_at).toLocaleString()}
                                    </span>
                                  </div>
                                  <span className={styles.itemContent}>{revision.content}</span>
                                  {index < revisions.length - 1 && revision.content !== revisions[index + 1].content && (
                                    <span className={styles.itemRationale}>Previous: {revisions[index + 1].content}</span>
                                  )}
                                  {revision.revision !== d.revision && (
                                    <Button size="small" disabled={busy} onClick={() => void restoreDecision(d, revision.revision)}>
                                      Restore as new revision
                                    </Button>
                                  )}
                                </div>
                              ))}
                              {decisionHistory[d.id].page < decisionHistory[d.id].total_pages && (
                                <Button
                                  size="small"
                                  disabled={busy}
                                  onClick={() => void loadDecisionHistory(d, decisionHistory[d.id].page + 1)}
                                >
                                  Load older revisions
                                </Button>
                              )}
                            </div>
                          )}
                        </div>
                      ))}
                    </div>
                    {decisionsTotalCount > decisionsPageSize && (
                      <Pager
                        page={decisionsPage}
                        pageSize={decisionsPageSize}
                        totalItems={decisionsTotalCount}
                        pageSizeOptions={[10, 25, 50]}
                        onPageChange={(p) => { setDecisionsPage(p); setDecisions(null); }}
                        onPageSizeChange={(size) => { setDecisionsPageSize(size); setDecisionsPage(1); setDecisions(null); }}
                      />
                    )}
                  </PageSection>
                )}

                {pending.length > 0 && (
                  <PageSection
                    title="Pending proposals"
                    description="Review proposals and merge, promote, or reject them."
                  >
                    <div className={styles.itemList}>
                      {pending.map(e => (
                        <div key={e.id} className={styles.proposedItem}>
                          <div className={styles.itemHeader}>
                            <span className={styles.itemTitle}>{e.title}</span>
                            <Badge appearance="tint" color="warning">Proposed</Badge>
                            <Badge appearance="tint" color="subtle">{e.type}</Badge>
                            <Badge appearance="outline">{e.agent_name}</Badge>
                            <span className={styles.itemMeta}>{new Date(e.created_at).toLocaleString()}</span>
                          </div>
                          <span className={styles.itemContent}>{e.content}</span>
                          {e.rationale && (
                            <span className={styles.itemRationale}>Rationale: {e.rationale}</span>
                          )}
                          <div className={styles.actions}>
                            <Button size="small" appearance="primary" disabled={busy} onClick={() => void runInboxAction(e.id, 'merge')}>Merge</Button>
                            <Button size="small" disabled={busy} onClick={() => void runInboxAction(e.id, 'promote')}>Promote</Button>
                            <Button size="small" appearance="outline" disabled={busy} onClick={() => void runInboxAction(e.id, 'reject')}>Reject</Button>
                          </div>
                        </div>
                      ))}
                    </div>
                    {inboxTotalCount > inboxPageSize && (
                      <Pager
                        page={inboxPage}
                        pageSize={inboxPageSize}
                        totalItems={inboxTotalCount}
                        pageSizeOptions={[10, 25, 50]}
                        onPageChange={(p) => { setInboxPage(p); setInbox(null); }}
                        onPageSizeChange={(size) => { setInboxPageSize(size); setInboxPage(1); setInbox(null); }}
                      />
                    )}
                  </PageSection>
                )}
              </>
            )
        )}

        {!loading && !loadError && selectedTab === 'memory' && (
          <>
            <PageSection title="Add a memory entry" description="Create durable operational learnings for agents.">
              <div className={styles.form}>
                <div className={styles.inlineFields}>
                  <Field label="Agent name" required>
                    <Input value={newAgentName} onChange={(_, data) => setNewAgentName(data.value)} disabled={busy} />
                  </Field>
                  <Field label="Type" required>
                    <Input value={newType} onChange={(_, data) => setNewType(data.value)} disabled={busy} />
                  </Field>
                </div>
                <Field label="Content" required>
                  <Textarea value={newContent} onChange={(_, data) => setNewContent(data.value)} disabled={busy} rows={4} />
                </Field>
                <Button
                  appearance="primary"
                  disabled={busy || !newAgentName.trim() || !newType.trim() || !newContent.trim()}
                  onClick={() => void createMemory()}
                >
                  Create memory
                </Button>
              </div>
            </PageSection>
            <PageSection title="Search team knowledge" description="Search current or historical memory with bounded, deterministic pages.">
              <div className={styles.form}>
                <div className={styles.inlineFields}>
                  <Field label="Text">
                    <Input value={memoryQuery} onChange={(_, data) => setMemoryQuery(data.value)} />
                  </Field>
                  <Field label="Lifecycle state">
                    <Input
                      value={memoryStatus}
                      onChange={(_, data) => setMemoryStatus(data.value.trim().toLowerCase() || 'active')}
                      placeholder="active, superseded, archived, or all"
                    />
                  </Field>
                </div>
                <Button
                  appearance="primary"
                  onClick={() => {
                    setAppliedMemoryQuery(memoryQuery.trim());
                    setMemoryPage(1);
                    setMemory(null);
                  }}
                >
                  Search
                </Button>
              </div>
            </PageSection>
            {memory === null || memory.length === 0
              ? (
                <EmptyState
                  title="No agent memory recorded yet"
                  description="Create a memory entry to capture durable team learnings."
                />
              )
              : (
                <div className={styles.itemList}>
                  {memory.map(m => (
                    <div key={m.id} className={styles.item}>
                      <div className={styles.itemHeader}>
                        <Badge appearance="outline">{m.agent_name}</Badge>
                        <Badge appearance="tint" color={
                          m.importance === 'high' ? 'danger' :
                          m.importance === 'medium' ? 'warning' : 'subtle'
                        }>{m.importance}</Badge>
                        <Badge appearance="outline">{m.type}</Badge>
                        <Badge appearance="outline">{m.status}</Badge>
                        <span className={styles.itemMeta}>Revision {m.revision}</span>
                        <span className={styles.itemMeta}>{new Date(m.created_at).toLocaleString()}</span>
                      </div>
                      {editingMemoryId === m.id ? (
                        <div className={styles.form}>
                          <Field label="Type" required>
                            <Input value={editType} onChange={(_, data) => setEditType(data.value)} disabled={busy} />
                          </Field>
                          <Field label="Content" required>
                            <Textarea value={editContent} onChange={(_, data) => setEditContent(data.value)} disabled={busy} rows={4} />
                          </Field>
                          <div className={styles.actions}>
                            <Button size="small" appearance="primary" disabled={busy || !editType.trim() || !editContent.trim()} onClick={() => void updateMemory(m)}>Save</Button>
                            <Button size="small" disabled={busy} onClick={() => setEditingMemoryId(null)}>Cancel</Button>
                          </div>
                        </div>
                      ) : (
                        <>
                          <span className={styles.itemContent}>{m.content}</span>
                          <div className={styles.actions}>
                            <Button size="small" disabled={busy} onClick={() => beginEditMemory(m)}>Update</Button>
                            <Button size="small" disabled={busy} onClick={() => void loadMemoryHistory(m)}>
                              {memoryHistory[m.id] ? 'Hide history' : 'History'}
                            </Button>
                          </div>
                          {memoryHistory[m.id] && (
                            <div className={styles.itemList}>
                              {memoryHistory[m.id].items.map((revision, index, revisions) => (
                                <div key={revision.revision_id} className={styles.proposedItem}>
                                  <div className={styles.itemHeader}>
                                    <strong>Revision {revision.revision}</strong>
                                    <Badge appearance="outline">{revision.status}</Badge>
                                    <span className={styles.itemMeta}>
                                      {revision.reason} by {revision.actor} · {new Date(revision.created_at).toLocaleString()}
                                    </span>
                                  </div>
                                  <span className={styles.itemContent}>{revision.content}</span>
                                  {index < revisions.length - 1 && revision.content !== revisions[index + 1].content && (
                                    <span className={styles.itemRationale}>
                                      Previous: {revisions[index + 1].content}
                                    </span>
                                  )}
                                  {revision.revision !== m.revision && (
                                    <Button size="small" disabled={busy} onClick={() => void restoreMemory(m, revision.revision)}>
                                      Restore as new revision
                                    </Button>
                                  )}
                                </div>
                              ))}
                              {memoryHistory[m.id].page < memoryHistory[m.id].total_pages && (
                                <Button
                                  size="small"
                                  disabled={busy}
                                  onClick={() => void loadMemoryHistory(m, memoryHistory[m.id].page + 1)}
                                >
                                  Load older revisions
                                </Button>
                              )}
                            </div>
                          )}
                        </>
                      )}
                    </div>
                  ))}
                  {memoryTotalCount > memoryPageSize && (
                    <Pager
                      page={memoryPage}
                      pageSize={memoryPageSize}
                      totalItems={memoryTotalCount}
                      pageSizeOptions={[10, 25, 50]}
                      onPageChange={(p) => { setMemoryPage(p); setMemory(null); }}
                      onPageSizeChange={(size) => { setMemoryPageSize(size); setMemoryPage(1); setMemory(null); }}
                    />
                  )}
                </div>
              )}
          </>
        )}

        {!loading && !loadError && selectedTab === 'sessions' && (
          sessions === null || sessions.length === 0
            ? (
              <EmptyState
                title="No session history yet"
                description="Completed and in-progress session summaries will appear here."
              />
            )
            : (
              <PageSection
                title="Session history"
                description="Recent coordinator and agent sessions for this project."
              >
                <div className={styles.itemList}>
                  {sessions.map((session) => {
                    const issues = parseActiveIssues(session.active_issues);
                    return (
                      <div key={session.id} className={styles.item}>
                        <div className={styles.itemHeader}>
                          <span className={styles.itemTitle}>{session.focus_area || session.session_id}</span>
                          <Badge appearance="outline">{session.ended_at ? 'Completed' : 'In progress'}</Badge>
                          <span className={styles.itemMeta}>
                            Started {new Date(session.started_at).toLocaleString()}
                            {session.ended_at ? ` • Ended ${new Date(session.ended_at).toLocaleString()}` : ''}
                          </span>
                        </div>
                        <span className={styles.itemMeta}>Session ID: {session.session_id}</span>
                        {session.summary && <span className={styles.itemContent}>{session.summary}</span>}
                        {issues.length > 0 && (
                          <span className={styles.itemRationale}>Active issues: {issues.join(', ')}</span>
                        )}
                      </div>
                    );
                  })}
                  {sessionsTotalCount > sessionsPageSize && (
                    <Pager
                      page={sessionsPage}
                      pageSize={sessionsPageSize}
                      totalItems={sessionsTotalCount}
                      pageSizeOptions={[10, 25, 50]}
                      onPageChange={(p) => { setSessionsPage(p); setSessions(null); }}
                      onPageSizeChange={(size) => { setSessionsPageSize(size); setSessionsPage(1); setSessions(null); }}
                    />
                  )}
                </div>
              </PageSection>
            )
        )}
      </div>
    </PageContainer>
  );
}