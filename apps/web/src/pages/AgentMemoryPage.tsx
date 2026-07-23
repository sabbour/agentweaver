import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { Badge, makeStyles, tokens } from '@fluentui/react-components';
import { EmptyState, ErrorState, LoadingState, MetricRow, PageContainer, PageHeader } from '../components/ui';
import { Pager } from '../copilot-fluent-system';
import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { AgentMemoryDto } from '../api/types';

const useStyles = makeStyles({
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  breadcrumbSep: {
    color: tokens.colorNeutralForeground4,
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
});

function formatApiError(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body || 'Request failed'}`;
  return err instanceof Error ? err.message : String(err);
}

export function AgentMemoryPage() {
  const styles = useStyles();
  const { projectId, agentName } = useParams<{ projectId: string; agentName: string }>();
  const [entries, setEntries] = useState<AgentMemoryDto[] | null>(null);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadNonce, setReloadNonce] = useState(0);

  useEffect(() => {
    if (!projectId || !agentName) return;
    let cancelled = false;
    const loadEntries = async () => {
      setLoading(true);
      setError(null);
      try {
        const result = await apiClient.getAgentMemory(projectId, agentName, { page, pageSize });
        if (cancelled) return;
        setEntries(result.items);
        setTotalCount(result.total_count);
      } catch (err: unknown) {
        if (cancelled) return;
        setEntries([]);
        setTotalCount(0);
        setError(formatApiError(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    void loadEntries();
    return () => { cancelled = true; };
  }, [agentName, page, pageSize, projectId, reloadNonce]);

  return (
    <PageContainer>
      <PageHeader
        title={`${agentName ?? 'Agent'} memory`}
        description="Durable memory entries captured for this agent."
        breadcrumbs={
          <>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <Link to={`/projects/${projectId}/team`} className={styles.breadcrumbLink}>Agents</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <span>{agentName ?? 'Memory'}</span>
          </>
        }
      />

      <MetricRow items={[{ label: 'Entries', value: totalCount }]} />

      {loading && <LoadingState rows={3} />}
      {error && <ErrorState message={error} onRetry={() => { setEntries(null); setLoading(true); setError(null); setReloadNonce((value) => value + 1); }} />}

      {!loading && !error && (entries === null || entries.length === 0) && (
        <EmptyState
          title="No memory entries yet"
          description="This agent has not captured any durable memory entries yet."
        />
      )}

      {!loading && !error && entries && entries.length > 0 && (
        <div className={styles.itemList}>
          {entries.map((entry) => (
            <div key={entry.id} className={styles.item}>
              <div className={styles.itemHeader}>
                <Badge appearance="tint">{entry.importance}</Badge>
                <Badge appearance="outline">{entry.type}</Badge>
                <span className={styles.itemMeta}>{new Date(entry.created_at).toLocaleString()}</span>
              </div>
              <span className={styles.itemContent}>{entry.content}</span>
            </div>
          ))}
          {totalCount > pageSize && (
            <Pager
              page={page}
              pageSize={pageSize}
              totalItems={totalCount}
              pageSizeOptions={[10, 25, 50]}
              onPageChange={(nextPage) => setPage(nextPage)}
              onPageSizeChange={(size) => { setPageSize(size); setPage(1); }}
            />
          )}
        </div>
      )}
    </PageContainer>
  );
}
