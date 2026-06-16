import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Spinner,
  Tab,
  TabList,
  Text,
  Title2,
  Badge,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { SelectTabData } from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import type { DecisionDto, AgentMemoryDto } from '../api/types';

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '900px',
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
  subtitle: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: '1.4',
    maxWidth: '640px',
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
});

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState<'decisions' | 'rai'>('decisions');
  const [decisions,   setDecisions]   = useState<DecisionDto[] | null>(null);
  const [raiMemory,   setRaiMemory]   = useState<AgentMemoryDto[] | null>(null);
  const [loading,     setLoading]     = useState(false);

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);

    if (selectedTab === 'decisions') {
      if (decisions !== null) { setLoading(false); return; }
      apiClient.getDecisions(projectId)
        .then(d => setDecisions(d))
        .catch(() => setDecisions([]))
        .finally(() => setLoading(false));
    } else {
      if (raiMemory !== null) { setLoading(false); return; }
      apiClient.getAgentMemory(projectId, 'Rai')
        .then(m => setRaiMemory(m))
        .catch(() => setRaiMemory([]))
        .finally(() => setLoading(false));
    }
  }, [projectId, selectedTab, decisions, raiMemory]);

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span>/</span>
        <span>Team Memory</span>
      </nav>

      <Title2>Team Memory</Title2>
      <Text className={styles.subtitle}>
        Scribe maintains team-wide decisions, session logs, and cross-agent context across every run.
        Rai maintains a responsible-AI audit trail.
      </Text>

      {/* Tabs */}
      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_e, d: SelectTabData) => setSelectedTab(d.value as 'decisions' | 'rai')}
      >
        <Tab value="decisions">Decisions &amp; Memory</Tab>
        <Tab value="rai">RAI Audit</Tab>
      </TabList>

      <div className={styles.tabContent}>
        {loading && <Spinner size="small" label="Loading…" />}

        {!loading && selectedTab === 'decisions' && (
          decisions === null || decisions.length === 0
            ? <Text className={styles.empty}>No decisions recorded yet.</Text>
            : (
              <div className={styles.itemList}>
                {decisions.map(d => (
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
            )
        )}

        {!loading && selectedTab === 'rai' && (
          raiMemory === null || raiMemory.length === 0
            ? <Text className={styles.empty}>No RAI audit entries recorded yet.</Text>
            : (
              <div className={styles.itemList}>
                {raiMemory.map(m => (
                  <div key={m.id} className={styles.item}>
                    <div className={styles.itemHeader}>
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

