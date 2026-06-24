import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Spinner,
  Tab,
  TabList,
  Text,
  Badge,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { SelectTabData } from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
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
});

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

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);

    if (selectedTab === 'decisions') {
      if (decisions !== null) { setLoading(false); return; }
      Promise.all([
        apiClient.getDecisions(projectId).catch(() => [] as DecisionDto[]),
        apiClient.getDecisionsInbox(projectId).catch(() => [] as DecisionInboxEntryDto[]),
      ])
        .then(([d, i]) => { setDecisions(d); setInbox(i); })
        .finally(() => setLoading(false));
    } else {
      if (memory !== null) { setLoading(false); return; }
      apiClient.getProjectMemory(projectId)
        .then(m => setMemory(m))
        .catch(() => setMemory([]))
        .finally(() => setLoading(false));
    }
  }, [projectId, selectedTab, decisions, memory]);

  const pending = (inbox ?? []).filter(e => e.status === 'pending');
  const hasActiveDecisions = decisions !== null && decisions.length > 0;

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

        {!loading && selectedTab === 'decisions' && (
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
                      The built-in Coordinator agent promotes these proposals into active Team Memory
                      on its own. No action is needed from you.
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
                      </div>
                    ))}
                  </section>
                )}
              </>
            )
        )}

        {!loading && selectedTab === 'memory' && (
          memory === null || memory.length === 0
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
                    <span className={styles.itemContent}>{m.content}</span>
                  </div>
                ))}
              </div>
            )
        )}
      </div>
    </div>
  );
}

