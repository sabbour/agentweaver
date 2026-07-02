import { Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import type { AgentUsageBreakdownDto, RunAgentTokenBreakdownDto } from '../../api/types';
import { costChipLabel } from '../CostChip';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  note: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
  },
  track: {
    height: '10px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandForeground1,
  },
});

function usageValue(entry: AgentUsageBreakdownDto): number {
  return entry.totalTokens > 0 ? entry.totalTokens : entry.totalNanoAiu;
}

export function AgentTokenBreakdown({
  data,
  title = 'Agent token breakdown',
  subtitle = 'Per-agent usage for this orchestration run.',
}: {
  data: RunAgentTokenBreakdownDto | null;
  title?: string;
  subtitle?: string;
}) {
  const styles = useStyles();
  const rows = data?.breakdown ?? [];
  const max = Math.max(1, ...rows.map(usageValue));
  const hasFallbackTotal = !data?.hasAgentData && ((data?.totalTokens ?? 0) > 0 || (data?.totalNanoAiu ?? 0) > 0);

  return (
    <div className={styles.panel}>
      <div>
        <Title3>{title}</Title3>
        <Text className={styles.subtitle}>{subtitle}</Text>
      </div>

      {!data ? (
        <Text>Loading usage…</Text>
      ) : rows.length === 0 && !hasFallbackTotal ? (
        <Text>No agent usage data yet.</Text>
      ) : (
        <div className={styles.list}>
          {rows.map((entry) => (
            <div key={entry.agentName} className={styles.row}>
              <div className={styles.rowHead}>
                <Text>{entry.agentName}</Text>
                <Text>{costChipLabel(entry.totalNanoAiu, entry.totalTokens) ?? `${entry.invocationCount} turns`}</Text>
              </div>
              <div className={styles.track}>
                <div className={styles.bar} style={{ width: `${Math.max(8, (usageValue(entry) / max) * 100)}%` }} />
              </div>
            </div>
          ))}
          {hasFallbackTotal && (
            <div className={styles.row}>
              <div className={styles.rowHead}>
                <Text>Total run usage</Text>
                <Text>{costChipLabel(data.totalNanoAiu, data.totalTokens) ?? '—'}</Text>
              </div>
              <div className={styles.track}>
                <div className={styles.bar} style={{ width: '100%' }} />
              </div>
            </div>
          )}
        </div>
      )}

      {data?.source === 'events' && (
        <Text className={styles.note}>Showing persisted turn-usage events because AppInsights agent dimensions are not available for this run yet.</Text>
      )}
    </div>
  );
}
