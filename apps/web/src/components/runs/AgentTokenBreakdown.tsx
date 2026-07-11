import {
  AzureEmptyState,
  BladeHeader,
  ProgressBarWithLabel,
  StatusIconText,
  Text } from '../../copilot-fluent-system';
import { AgentIdentity } from '../AgentIdentity';
import { costChipLabel } from '../CostChip';
import { makeStyles,
  mergeClasses,
  tokens,
} from '../../copilot-fluent-system';
import type { AgentUsageBreakdownDto, RunAgentTokenBreakdownDto } from '../../api/types';
const useStyles = makeStyles({
  panel: {
    minWidth: 0,
  },
  note: {
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
    lineHeight: '20px',
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-start',
  },
  identity: {
    flex: 1,
    minWidth: 0,
  },
  metric: {
    whiteSpace: 'nowrap',
  },
  progress: {
    maxWidth: 'none',
  },
});

function usageValue(entry: AgentUsageBreakdownDto): number {
  return entry.totalTokens > 0 ? entry.totalTokens : entry.totalNanoAiu;
}

export function AgentTokenBreakdown({
  data,
  title = 'Agent token breakdown',
  subtitle = 'Per-agent usage for this orchestration run.',
  roleByAgent,
}: {
  data: RunAgentTokenBreakdownDto | null;
  title?: string;
  subtitle?: string;
  roleByAgent?: Record<string, string>;
}) {
  const styles = useStyles();
  const rows = data?.breakdown ?? [];
  const max = Math.max(1, ...rows.map(usageValue));
  const hasFallbackTotal = !data?.hasAgentData && ((data?.totalTokens ?? 0) > 0 || (data?.totalNanoAiu ?? 0) > 0);

  return (
    <div className={mergeClasses('azf-surface azf-surface--panel azf-surface--padding-comfortable azf-stack azf-gap-m', styles.panel)}>
      <BladeHeader size="compact" title={title} subtitle={subtitle} />

      {!data ? (
        <AzureEmptyState compact title="Loading usage…" />
      ) : rows.length === 0 && !hasFallbackTotal ? (
        <AzureEmptyState compact title="No agent usage data yet." />
      ) : (
        <div className="azf-stack azf-gap-s">
          {rows.map((entry) => (
            <div key={entry.agentName} className="azf-stack azf-gap-xs">
              <div className={styles.rowHead}>
                <AgentIdentity label={entry.agentName} roleByAgent={roleByAgent} className={styles.identity} />
                <Text className={styles.metric}>{costChipLabel(entry.totalNanoAiu, entry.totalTokens) ?? `${entry.invocationCount} turns`}</Text>
              </div>
              <ProgressBarWithLabel
                className={styles.progress}
                value={Math.max(0.08, usageValue(entry) / max)}
                max={1}
                thickness="large"
              />
            </div>
          ))}
          {hasFallbackTotal && (
            <div className="azf-stack azf-gap-xs">
              <div className={styles.rowHead}>
                <Text>Total run usage</Text>
                <Text>{costChipLabel(data.totalNanoAiu, data.totalTokens) ?? '—'}</Text>
              </div>
              <ProgressBarWithLabel className={styles.progress} value={1} max={1} thickness="large" />
            </div>
          )}
        </div>
      )}

      {data?.source === 'events' && (
        <StatusIconText status="info" className={styles.note}>Showing persisted turn-usage events because AppInsights agent dimensions are not available for this run yet.</StatusIconText>
      )}
    </div>
  );
}
