import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Spinner,
  Tab,
  TabList,
  Text,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { SelectTabData } from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import type { HistoryDto } from '../api/types';

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
  prose: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '1.7',
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    whiteSpace: 'pre-wrap',
    overflowY: 'auto',
    maxHeight: '65vh',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

// ---------------------------------------------------------------------------
// Tabs
// ---------------------------------------------------------------------------

const TABS: { value: string; label: string; member: string; description: string }[] = [
  {
    value:       'scribe-decisions',
    label:       'Decisions & Memory',
    member:      'Scribe',
    description: 'Team-wide decisions, scope choices, and architecture notes recorded by Scribe across all sessions.',
  },
  {
    value:       'rai-history',
    label:       'RAI Audit',
    member:      'Rai',
    description: 'Responsible AI review findings and audit trail maintained by Rai.',
  },
];

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState(TABS[0].value);
  const [history,     setHistory]     = useState<Record<string, HistoryDto>>({});
  const [loading,     setLoading]     = useState<Record<string, boolean>>({});

  const currentTab = TABS.find(t => t.value === selectedTab) ?? TABS[0];

  // Load history for the selected tab's member whenever tab changes
  useEffect(() => {
    if (!projectId) return;
    const member = currentTab.member;
    if (history[member]) return; // already fetched

    setLoading(prev => ({ ...prev, [member]: true }));
    apiClient.getMemberHistory(projectId, member)
      .then(h => setHistory(prev => ({ ...prev, [member]: h })))
      .catch(() => setHistory(prev => ({ ...prev, [member]: { member_name: member, content: '' } })))
      .finally(() => setLoading(prev => ({ ...prev, [member]: false })));
  }, [projectId, currentTab.member, history]);

  const currentHistory = history[currentTab.member];
  const isLoading      = loading[currentTab.member] ?? false;

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
        onTabSelect={(_e, d: SelectTabData) => setSelectedTab(String(d.value))}
      >
        {TABS.map(t => (
          <Tab key={t.value} value={t.value}>{t.label}</Tab>
        ))}
      </TabList>

      <div className={styles.tabContent}>
        <Text className={styles.subtitle} style={{ marginBottom: tokens.spacingVerticalS }}>
          {currentTab.description}
        </Text>

        {isLoading && <Spinner size="small" label="Loading…" />}

        {!isLoading && !currentHistory?.content && (
          <Text className={styles.empty}>Nothing recorded yet.</Text>
        )}

        {!isLoading && currentHistory?.content && (
          <pre className={styles.prose}>{currentHistory.content}</pre>
        )}
      </div>
    </div>
  );
}

