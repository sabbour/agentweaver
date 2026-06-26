import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Tab,
  TabList,
  Text,
  Badge,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { SelectTabData } from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { DecisionDto, AgentMemoryDto, DecisionInboxEntryDto } from '../api/types';
import { PageHeader } from '../components/PageHeader';

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  tabContent: {
    marginTop: tokens.spacingVerticalM,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  itemList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  item: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground2,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
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
  proposedSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    marginTop: tokens.spacingVerticalL,
  },
  proposedHeading: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  proposedCaption: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '1.4',
    maxWidth: '640px',
  },
  proposedItem: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
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
    marginBottom: tokens.spacingVerticalL,
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

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState<'decisions' | 'memory'>('decisions');
  const [decisions,   setDecisions]   = useState<DecisionDto[] | null>(null);
  const [inbox,       setInbox]       = useState<DecisionInboxEntryDto[] | null>(null);
  const [memory,      setMemory]      = useState<AgentMemoryDto[] | null>(null);
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

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);
    setLoadError(null);

    if (selectedTab === 'decisions') {
      if (decisions !== null && inbox !== null) { setLoading(false); return; }
      Promise.all([
        apiClient.getDecisions(projectId),
        apiClient.getDecisionsInbox(projectId),
      ])
        .then(([d, i]) => { setDecisions(d); setInbox(i); })
        .catch((err: unknown) => { setDecisions([]); setInbox([]); setLoadError(formatApiError(err)); })
        .finally(() => setLoading(false));
    } else {
      if (memory !== null) { setLoading(false); return; }
      apiClient.getProjectMemory(projectId)
        .then(m => setMemory(m))
        .catch((err: unknown) => { setMemory([]); setLoadError(formatApiError(err)); })
        .finally(() => setLoading(false));
    }
  }, [projectId, selectedTab, decisions, inbox, memory, reloadKey]);

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

  const pending = (inbox ?? []).filter(e => e.status === 'pending');
  const hasActiveDecisions = decisions !== null && decisions.length > 0;
  const busy = busyAction !== null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Team Memory"
        subtitle="Decisions and learnings the team has captured."
        breadcrumb={
          <nav className={styles.breadcrumb} aria-label="Breadcrumb">
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
            <span>/</span>
            <span>Team Memory</span>
          </nav>
        }
      />

      {/* Tabs */}
      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_e, d: SelectTabData) => setSelectedTab(d.value as 'decisions' | 'memory')}
      >
        <Tab value="decisions">Decisions</Tab>
        <Tab value="memory">Agent Memory</Tab>
      </TabList>

      <div className={styles.tabContent}>
        {loading && <Spinner size="small" label="Loading…" />}
        {loadError && (
          <MessageBar intent="error">
            <MessageBarBody>{loadError}</MessageBarBody>
            <Button size="small" onClick={retryLoad}>Retry</Button>
          </MessageBar>
        )}
        {mutationError && (
          <MessageBar intent="error">
            <MessageBarBody>{mutationError}</MessageBarBody>
          </MessageBar>
        )}

        {!loading && !loadError && selectedTab === 'decisions' && (
          !hasActiveDecisions && pending.length === 0
            ? <Text className={styles.empty}>No decisions recorded yet.</Text>
            : (
              <>
                {hasActiveDecisions && (
                  <div className={styles.itemList}>
                    {decisions!.map(d => (
                      <div key={d.id} className={styles.item}>
                        <div className={styles.itemHeader}>
                          <span className={styles.itemTitle}>{d.title}</span>
                          <Badge appearance="tint" color="informative">{d.type}</Badge>
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
                )}

                {pending.length > 0 && (
                  <section className={styles.proposedSection} aria-label="Proposed decisions awaiting Coordinator">
                    <span className={styles.proposedHeading}>Proposed — awaiting Coordinator</span>
                    <Text className={styles.proposedCaption}>
                      Review pending proposals and merge, promote, or reject them.
                    </Text>
                    {pending.map(e => (
                      <div key={e.id} className={styles.proposedItem}>
                        <div className={styles.itemHeader}>
                          <span className={styles.itemTitle}>{e.title}</span>
                          <Badge appearance="tint" color="warning">Proposed</Badge>
                          <Badge appearance="tint" color="informative">{e.type}</Badge>
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
                  </section>
                )}
              </>
            )
        )}

        {!loading && !loadError && selectedTab === 'memory' && (
          <>
            <section className={styles.form} aria-label="Create memory entry">
              <Text weight="semibold">Create memory entry</Text>
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
              <Button appearance="primary" disabled={busy || !newAgentName.trim() || !newType.trim() || !newContent.trim()} onClick={() => void createMemory()}>
                Create memory
              </Button>
            </section>
            {memory === null || memory.length === 0
              ? <Text className={styles.empty}>No agent memory recorded yet.</Text>
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
                </div>
              )}
          </>
        )}
      </div>
    </div>
  );
}
