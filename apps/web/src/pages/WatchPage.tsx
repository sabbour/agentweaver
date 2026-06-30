import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Text, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import type { TokenUsageSummary } from '../api/types';
import { RunWatcher } from '../components/RunWatcher';
import { TokenUsagePanel } from '../components/TokenUsagePanel';

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
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
  },
  idLabel: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

const short = (id: string) => id.slice(0, 8);

export function WatchPage() {
  const styles = useStyles();

  // Route: /projects/:projectId/runs/:runId/execution/:executionId
  const { projectId, runId: workflowRunId, executionId } = useParams<{
    projectId: string;
    runId: string;
    executionId: string;
  }>();

  const [runUsage, setRunUsage] = useState<TokenUsageSummary | null>(null);

  const fetchUsage = useCallback(() => {
    if (!executionId) return;
    void apiClient.getRunUsage(executionId).then(setRunUsage).catch(() => {
      // Usage is supplementary — degrade gracefully on error.
    });
  }, [executionId]);

  useEffect(() => {
    fetchUsage();
  }, [fetchUsage]);

  if (!executionId) {
    return <Text>No execution id provided.</Text>;
  }

  return (
    <div className={styles.root}>
      <nav className={styles.breadcrumb} aria-label="Breadcrumb">
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}/runs/${workflowRunId}/workflow`} className={styles.breadcrumbLink}>
          Run {short(workflowRunId!)}
        </Link>
        <span>/</span>
        <span>Execution {short(executionId)}</span>
      </nav>

      <div className={styles.headerRow}>
        <Title2>Execution</Title2>
        <span className={styles.idLabel}>{short(executionId)}</span>
      </div>

      <RunWatcher key={executionId} runId={executionId} onTurnUsageEvent={fetchUsage} />

      {runUsage && <TokenUsagePanel usage={runUsage} title="Token usage" />}
    </div>
  );
}
