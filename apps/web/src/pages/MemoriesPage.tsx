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
import type { TeamDto, HistoryDto } from '../api/types';

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
  tabContent: {
    marginTop: tokens.spacingVerticalM,
  },
  memberList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  memberButton: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground1,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground2,
    },
  },
  memberName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
  },
  memberRole: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  historyPane: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  historyHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  prose: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: '1.6',
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    whiteSpace: 'pre-wrap',
    overflowY: 'auto',
    maxHeight: '60vh',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function MemoriesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [team,         setTeam]         = useState<TeamDto | null>(null);
  const [loading,      setLoading]      = useState(true);
  const [error,        setError]        = useState<string | null>(null);
  const [selectedTab,  setSelectedTab]  = useState<string>('Scribe');
  const [history,      setHistory]      = useState<Record<string, HistoryDto>>({});
  const [histLoading,  setHistLoading]  = useState<Record<string, boolean>>({});

  // Load team roster
  useEffect(() => {
    if (!projectId) return;
    apiClient.getTeam(projectId)
      .then(t => { setTeam(t); setLoading(false); })
      .catch(() => { setError('Could not load team.'); setLoading(false); });
  }, [projectId]);

  // Load history whenever selected tab changes
  useEffect(() => {
    if (!projectId || !selectedTab) return;
    if (history[selectedTab]) return; // already fetched

    setHistLoading(prev => ({ ...prev, [selectedTab]: true }));
    apiClient.getMemberHistory(projectId, selectedTab)
      .then(h => setHistory(prev => ({ ...prev, [selectedTab]: h })))
      .catch(() => setHistory(prev => ({ ...prev, [selectedTab]: { member_name: selectedTab, content: '' } })))
      .finally(() => setHistLoading(prev => ({ ...prev, [selectedTab]: false })));
  }, [projectId, selectedTab, history]);

  if (loading) return <Spinner label="Loading memories…" />;
  if (error)   return <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>;
  if (!team)   return null;

  // Put Scribe first; then other built-in members; then cast agents
  const members = [...team.members].sort((a, b) => {
    if (a.name === 'Scribe') return -1;
    if (b.name === 'Scribe') return 1;
    if (a.is_built_in && !b.is_built_in) return -1;
    if (!a.is_built_in && b.is_built_in) return 1;
    return a.name.localeCompare(b.name);
  }).filter(m => m.status === 'active');

  const currentHistory = history[selectedTab];
  const isLoading      = histLoading[selectedTab] ?? false;

  return (
    <div className={styles.root}>
      {/* Breadcrumb */}
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}/team`} className={styles.breadcrumbLink}>Team</Link>
        <span>/</span>
        <span>Memories</span>
      </nav>

      <Title2>Team Memories</Title2>
      <Text style={{ color: tokens.colorNeutralForeground2 }}>
        Memories, decisions, and history recorded by each team member across sessions.
      </Text>

      {/* Member tabs */}
      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_e, d: SelectTabData) => setSelectedTab(String(d.value))}
      >
        {members.map(m => (
          <Tab key={m.name} value={m.name}>
            {m.name}
          </Tab>
        ))}
      </TabList>

      <div className={styles.tabContent}>
        {isLoading && <Spinner size="small" label={`Loading ${selectedTab}'s memories…`} />}

        {!isLoading && !currentHistory?.content && (
          <Text className={styles.empty}>No memories recorded yet for {selectedTab}.</Text>
        )}

        {!isLoading && currentHistory?.content && (
          <div className={styles.historyPane}>
            <div className={styles.historyHeader}>
              <Text weight="semibold">{selectedTab}</Text>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                {members.find(m => m.name === selectedTab)?.role_title}
              </Text>
            </div>
            <pre className={styles.prose}>{currentHistory.content}</pre>
          </div>
        )}
      </div>
    </div>
  );
}
