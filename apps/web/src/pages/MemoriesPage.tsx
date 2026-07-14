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
import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { AgentMemoryDto, DecisionDto, DecisionInboxEntryDto } from '../api/types';

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

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState<'decisions' | 'memory'>('decisions');
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

  const loadDecisionsPage = async () => {
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
  };

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);
    setLoadError(null);

    if (selectedTab === 'decisions') {
      if (decisions !== null && inbox !== null) { setLoading(false); return; }
      loadDecisionsPage()
        .catch((err: unknown) => { setDecisions([]); setDecisionsTotalCount(0); setInbox([]); setInboxTotalCount(0); setLoadError(formatApiError(err)); })
        .finally(() => setLoading(false));
    } else {
      if (memory !== null) { setLoading(false); return; }
      apiClient.getProjectMemory(projectId, { page: memoryPage, pageSize: memoryPageSize })
        .then(m => { setMemory(m.items); setMemoryTotalCount(m.total_count); })
        .catch((err: unknown) => { setMemory([]); setMemoryTotalCount(0); setLoadError(formatApiError(err)); })
        .finally(() => setLoading(false));
    }
  }, [projectId, selectedTab, decisions, inbox, memory, reloadKey, decisionsPage, decisionsPageSize, inboxPage, inboxPageSize, memoryPage, memoryPageSize]);

  const retryLoad = () => {
    if (selectedTab === 'decisions') {
      setDecisions(null);
      setInbox(null);
    } else {
      setMemory(null);
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
      await apiClient.updateAgentMemory(projectId, entry.agent_name, entry.id, { type: editType.trim(), content: editContent.trim() });
      setEditingMemoryId(null);
      refreshMemory();
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
      ]} />

      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_, data) => setSelectedTab(data.value as 'decisions' | 'memory')}
      >
        <Tab value="decisions">Decisions</Tab>
        <Tab value="memory">Agent memory</Tab>
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
                            <span className={styles.itemMeta}>{new Date(d.created_at).toLocaleString()}</span>
                          </div>
                          <span className={styles.itemContent}>{d.content}</span>
                          {d.rationale && (
                            <span className={styles.itemRationale}>Rationale: {d.rationale}</span>
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
                          </div>
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
      </div>
    </PageContainer>
  );
}