import { Badge, Text, makeStyles, tokens } from '@fluentui/react-components';
import type { AiCreditUsagePointDto, DailyInvocationPointDto, MetricPercentilesDto, ModelUsageBreakdownDto, ProjectMetricsDto } from '../../api/types';
import { costChipLabel } from '../CostChip';
import { MetricCardHeader, MetricEmptyState } from '../MetricTypography';

const useStyles = makeStyles({
  diagnostics: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.1fr) minmax(320px, .9fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    minWidth: 0,
  },
  trendPanel: {
    display: 'grid',
    gridTemplateRows: 'auto 1fr',
  },
  chartStack: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  chartSlot: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
    paddingTop: tokens.spacingVerticalS,
  },
  slotTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  latencyPanel: {
    gridColumn: '1 / -1',
  },
  latencyGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalL,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  subsection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    minWidth: 0,
  },
  pendingPanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
  },
  emptyCopy: {
    maxWidth: '72ch',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  rowMain: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  labelRow: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
  },
  barTrack: {
    width: '100%',
    height: '8px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  bar: {
    height: '100%',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandForeground1,
  },
  statGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto auto',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
});

function LineChart({
  points,
  valueOf,
  formatMax,
  label,
}: {
  points: Array<DailyInvocationPointDto | AiCreditUsagePointDto>;
  valueOf: (point: DailyInvocationPointDto | AiCreditUsagePointDto) => number;
  formatMax?: (value: number) => string;
  label: string;
}) {
  const width = 360;
  const height = 132;
  const pad = { top: 12, right: 12, bottom: 24, left: 28 };
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const max = Math.max(1, ...points.map(valueOf));
  const x = (index: number) => pad.left + ((points.length <= 1 ? 0 : index / (points.length - 1)) * innerW);
  const y = (value: number) => pad.top + innerH - ((value / max) * innerH);
  const path = points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${x(index).toFixed(1)},${y(valueOf(point)).toFixed(1)}`)
    .join(' ');

  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} role="img" aria-label={label}>
      <line
        x1={pad.left}
        y1={pad.top + innerH}
        x2={pad.left + innerW}
        y2={pad.top + innerH}
        stroke={tokens.colorNeutralStroke2}
        strokeWidth={1}
      />
      <text x={pad.left - 6} y={pad.top + 8} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {formatMax ? formatMax(max) : max}
      </text>
      <text x={pad.left} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3}>
        {points[0]?.date ?? ''}
      </text>
      <text x={pad.left + innerW} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {points.at(-1)?.date ?? ''}
      </text>
      <path d={path} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2} />
    </svg>
  );
}

function BarList({
  rows,
  valueOf,
  valueLabel,
}: {
  rows: ModelUsageBreakdownDto[];
  valueOf: (row: ModelUsageBreakdownDto) => number;
  valueLabel: (row: ModelUsageBreakdownDto) => string;
}) {
  const styles = useStyles();
  const max = Math.max(1, ...rows.map(valueOf));

  return (
    <div className={styles.list}>
      {rows.map((row) => {
        const value = valueOf(row);
        return (
          <div key={row.model} className={styles.row}>
            <div className={styles.rowMain}>
              <div className={styles.labelRow}>
                <Text>{row.model}</Text>
                <Text>{valueLabel(row)}</Text>
              </div>
              <div className={styles.barTrack}>
                <div className={styles.bar} style={{ width: `${Math.max(6, (value / max) * 100)}%` }} />
              </div>
            </div>
            <Badge appearance="outline">{row.invocationCount} calls</Badge>
          </div>
        );
      })}
    </div>
  );
}

function PercentilesTable({ rows, emptyLabel }: { rows: MetricPercentilesDto[]; emptyLabel: string }) {
  const styles = useStyles();

  if (rows.length === 0) {
    return <MetricEmptyState>{emptyLabel}</MetricEmptyState>;
  }

  return (
    <div className={styles.list}>
      {rows.map((row) => (
        <div key={row.label} className={styles.statGrid}>
          <Text>{row.label}</Text>
          <Badge appearance="tint">P50 {row.p50Ms != null ? `${Math.round(row.p50Ms)} ms` : '—'}</Badge>
          <Badge appearance="outline">P95 {row.p95Ms != null ? `${Math.round(row.p95Ms)} ms` : '—'}</Badge>
        </div>
      ))}
    </div>
  );
}

export function ModelPerformancePanels({ metrics }: { metrics: ProjectMetricsDto | null }) {
  const styles = useStyles();
  const invocationTrend = metrics?.invocationTrend ?? [];
  const aiCreditUsageTrend = metrics?.aiCreditUsageTrend ?? [];
  const modelUsage = metrics?.modelUsage ?? [];
  const responseDuration = metrics?.responseDuration ?? [];
  const ttft = metrics?.timeToFirstToken ?? [];
  const totalInvocations = modelUsage.reduce((sum, row) => sum + row.invocationCount, 0);
  const hasInvocationTrend = invocationTrend.some((point) => point.count > 0);
  const hasAiCreditTrend = aiCreditUsageTrend.some((point) => point.totalNanoAiu > 0);
  const hasModelUsage = modelUsage.some((row) => row.invocationCount > 0 || row.totalNanoAiu > 0);
  const hasResponseDuration = responseDuration.some((row) => row.p50Ms != null || row.p95Ms != null);
  const hasTtft = ttft.some((row) => row.p50Ms != null || row.p95Ms != null);
  const hasAnyTelemetry = hasAiCreditTrend || hasModelUsage || hasResponseDuration || hasTtft;

  if (!hasAnyTelemetry) {
    return (
      <div className={styles.pendingPanel}>
        <MetricCardHeader
          title="Model telemetry pending"
          subtitle="No model usage, AI credit, or latency signals are available for this range yet."
        />
        <MetricEmptyState className={styles.emptyCopy}>
          Run or complete an agent task to populate model mix, AI credit usage, response duration, and first-token timing.
        </MetricEmptyState>
      </div>
    );
  }

  return (
    <div className={styles.diagnostics}>
      <div className={`${styles.panel} ${styles.trendPanel}`}>
        <MetricCardHeader title="Model signal trend" subtitle="Run creation is shown as activity context; AI credit movement is the model evidence used for optimization." />
        <div className={styles.chartStack}>
          <div className={styles.chartSlot}>
            <Text className={styles.slotTitle}>Runs created</Text>
            {invocationTrend.length === 0 || !hasInvocationTrend ? (
              <MetricEmptyState>No run-creation data yet.</MetricEmptyState>
            ) : (
              <LineChart points={invocationTrend} valueOf={(point) => 'count' in point ? point.count : 0} label="Runs created over time" />
            )}
          </div>
          <div className={styles.chartSlot}>
            <Text className={styles.slotTitle}>AI credit usage</Text>
            {aiCreditUsageTrend.length === 0 || !hasAiCreditTrend ? (
              <MetricEmptyState>No AI credit usage data yet.</MetricEmptyState>
            ) : (
              <LineChart
                points={aiCreditUsageTrend}
                valueOf={(point) => 'totalNanoAiu' in point ? point.totalNanoAiu : 0}
                formatMax={(value) => costChipLabel(value, 0)?.replace(' AIC', '') ?? String(value)}
                label="AI credit usage over time"
              />
            )}
          </div>
        </div>
      </div>

      <div className={styles.panel}>
        <MetricCardHeader title="Model mix" subtitle="Compare spend and invocation share for the models that have emitted usage events." />
        <div className={styles.subsection}>
          <Text className={styles.slotTitle}>AI credit usage by model</Text>
          {modelUsage.length === 0 ? (
            <MetricEmptyState>No AI credit usage data yet.</MetricEmptyState>
          ) : (
            <BarList
              rows={modelUsage}
              valueOf={(row) => row.totalNanoAiu}
              valueLabel={(row) => costChipLabel(row.totalNanoAiu, 0) ?? '—'}
            />
          )}
        </div>
        <div className={styles.subsection}>
          <Text className={styles.slotTitle}>Invocation share</Text>
          {modelUsage.length === 0 ? (
            <MetricEmptyState>No model invocation data yet.</MetricEmptyState>
          ) : (
            <BarList
              rows={modelUsage}
              valueOf={(row) => row.invocationCount}
              valueLabel={(row) => `${totalInvocations > 0 ? Math.round((row.invocationCount / totalInvocations) * 100) : 0}%`}
            />
          )}
        </div>
      </div>

      <div className={`${styles.panel} ${styles.latencyPanel}`}>
        <MetricCardHeader title="Latency checkpoints" subtitle="P50 and P95 duration/TTFT stay grouped so slow models are visible without another card row." />
        <div className={styles.latencyGrid}>
          <div className={styles.subsection}>
            <Text className={styles.slotTitle}>Response duration by model</Text>
            <PercentilesTable rows={responseDuration} emptyLabel="No response-duration data yet." />
          </div>
          <div className={styles.subsection}>
            <Text className={styles.slotTitle}>Time to first token by model</Text>
            <PercentilesTable rows={ttft} emptyLabel="No TTFT data available yet." />
          </div>
        </div>
      </div>
    </div>
  );
}
