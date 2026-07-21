import { useState } from 'react';
import {
  Badge,
  Button,
  makeStyles,
  mergeClasses,
  ProgressBar,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ChevronDownRegular, ChevronUpRegular } from '@fluentui/react-icons';
import { costChipLabel } from '../costChipFormat';
import { Body, EmptyState, TitleText } from '../ui';
import type {
  AiCreditUsagePointDto,
  DailyInvocationPointDto,
  MetricPercentilesDto,
  ModelUsageBreakdownDto,
  ProjectMetricsDto,
} from '../../api/types';

const CHART_LINE = '#635c57';

const useStyles = makeStyles({
  diagnostics: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.1fr) minmax(320px, .9fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
  panel: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  trendPanel: {
    display: 'grid',
    gridTemplateRows: 'auto 1fr',
  },
  pendingPanel: {
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  panelHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
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
  emptyCopy: {
    maxWidth: '72ch',
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
  progress: {
    maxWidth: 'none',
  },
  barList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
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
      <path d={path} fill="none" stroke={CHART_LINE} strokeWidth={2} />
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
    <div className={styles.barList}>
      {rows.map((row) => {
        const value = valueOf(row);
        const progress = value <= 0 ? 0 : Math.max(0.06, value / max);
        return (
          <div key={row.model} className={styles.row}>
            <div className={styles.rowMain}>
              <div className={styles.labelRow}>
                <Text>{row.model}</Text>
                <Text>{valueLabel(row)}</Text>
              </div>
              <ProgressBar className={styles.progress} value={progress} max={1} thickness="large" />
            </div>
            <Badge appearance="outline" color="subtle">{row.invocationCount} calls</Badge>
          </div>
        );
      })}
    </div>
  );
}

function PercentilesTable({ rows, emptyLabel }: { rows: MetricPercentilesDto[]; emptyLabel: string }) {
  const [sortKey, setSortKey] = useState<'label' | 'p50' | 'p95'>('label');
  const [sortAsc, setSortAsc] = useState(true);

  if (rows.length === 0) return <EmptyState title={emptyLabel} />;

  const sorted = [...rows].sort((a, b) => {
    const av = sortKey === 'label' ? a.label : sortKey === 'p50' ? (a.p50Ms ?? null) : (a.p95Ms ?? null);
    const bv = sortKey === 'label' ? b.label : sortKey === 'p50' ? (b.p50Ms ?? null) : (b.p95Ms ?? null);
    if (av == null && bv == null) return 0;
    if (av == null) return 1;
    if (bv == null) return -1;
    const r = typeof av === 'string'
      ? av.localeCompare(bv as string, undefined, { numeric: true, sensitivity: 'base' })
      : (av as number) - (bv as number);
    return sortAsc ? r : -r;
  });

  const sortIcon = (key: 'label' | 'p50' | 'p95') =>
    sortKey === key && !sortAsc ? <ChevronUpRegular /> : <ChevronDownRegular />;

  const handleSort = (key: 'label' | 'p50' | 'p95') => {
    if (key === sortKey) setSortAsc((a) => !a);
    else { setSortKey(key); setSortAsc(true); }
  };

  return (
    <Table aria-label="Latency percentiles" size="small">
      <TableHeader>
        <TableRow>
          <TableHeaderCell
            aria-sort={sortKey === 'label' ? (sortAsc ? 'ascending' : 'descending') : 'none'}
          >
            <Button appearance="transparent" iconPosition="after" icon={sortIcon('label')} onClick={() => handleSort('label')}>
              Model
            </Button>
          </TableHeaderCell>
          <TableHeaderCell
            style={{ width: '120px' }}
            aria-sort={sortKey === 'p50' ? (sortAsc ? 'ascending' : 'descending') : 'none'}
          >
            <Button appearance="transparent" iconPosition="after" icon={sortIcon('p50')} onClick={() => handleSort('p50')}>
              P50
            </Button>
          </TableHeaderCell>
          <TableHeaderCell
            style={{ width: '120px' }}
            aria-sort={sortKey === 'p95' ? (sortAsc ? 'ascending' : 'descending') : 'none'}
          >
            <Button appearance="transparent" iconPosition="after" icon={sortIcon('p95')} onClick={() => handleSort('p95')}>
              P95
            </Button>
          </TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {sorted.map((row) => (
          <TableRow key={row.label}>
            <TableCell><Text>{row.label}</Text></TableCell>
            <TableCell><Text>{row.p50Ms != null ? `${Math.round(row.p50Ms)} ms` : '—'}</Text></TableCell>
            <TableCell><Text>{row.p95Ms != null ? `${Math.round(row.p95Ms)} ms` : '—'}</Text></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
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
        <div className={styles.panelHeader}>
          <TitleText as="h2">Model usage pending</TitleText>
          <Body tone="muted">No model usage, AI credit, or latency signals are available for this range yet.</Body>
        </div>
        <EmptyState
          className={styles.emptyCopy}
          title="No data yet"
          description="Run or complete an agent task to populate model mix, AI credit usage, response duration, and first-token timing."
        />
      </div>
    );
  }

  return (
    <div className={styles.diagnostics}>
      <div className={mergeClasses(styles.panel, styles.trendPanel)}>
        <div className={styles.panelHeader}>
          <TitleText as="h2">Model signal trend</TitleText>
          <Body tone="muted">Run creation is shown as activity context; AI credit movement is the model evidence used for optimization.</Body>
        </div>
        <div className={styles.chartStack}>
          <div className={styles.chartSlot}>
            <Text className={styles.slotTitle}>Runs created</Text>
            {invocationTrend.length === 0 || !hasInvocationTrend ? (
              <EmptyState title="No run-creation data yet." />
            ) : (
              <LineChart points={invocationTrend} valueOf={(point) => 'count' in point ? point.count : 0} label="Runs created over time" />
            )}
          </div>
          <div className={styles.chartSlot}>
            <Text className={styles.slotTitle}>AI credit usage</Text>
            {aiCreditUsageTrend.length === 0 || !hasAiCreditTrend ? (
              <EmptyState title="No AI credit usage data yet." />
            ) : (
              // TODO(ai-credits): chart axis formatter needs a plain string, not the <AiCredits> control — left as text.
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
        <div className={styles.panelHeader}>
          <TitleText as="h2">Model mix</TitleText>
          <Body tone="muted">Compare spend and invocation share for the models that have emitted usage events.</Body>
        </div>
        <div className={styles.subsection}>
          <Text className={styles.slotTitle}>AI credit usage by model</Text>
          {modelUsage.length === 0 ? (
            <EmptyState title="No AI credit usage data yet." />
          ) : (
            // TODO(ai-credits): BarList valueLabel requires a plain string, not the <AiCredits> hover control — left as text.
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
            <EmptyState title="No model invocation data yet." />
          ) : (
            <BarList
              rows={modelUsage}
              valueOf={(row) => row.invocationCount}
              valueLabel={(row) => `${totalInvocations > 0 ? Math.round((row.invocationCount / totalInvocations) * 100) : 0}%`}
            />
          )}
        </div>
      </div>

      <div className={mergeClasses(styles.panel, styles.latencyPanel)}>
        <div className={styles.panelHeader}>
          <TitleText as="h2">Latency checkpoints</TitleText>
          <Body tone="muted">P50 and P95 duration and first-token timing stay grouped so slow models are visible without another card row.</Body>
        </div>
        <div className={styles.latencyGrid}>
          <div className={styles.subsection}>
            <Text className={styles.slotTitle}>Response duration by model</Text>
            <PercentilesTable rows={responseDuration} emptyLabel="No response-duration data yet." />
          </div>
          <div className={styles.subsection}>
            <Text className={styles.slotTitle}>Time to first token by model</Text>
            <PercentilesTable rows={ttft} emptyLabel="No first-token timing data available yet." />
          </div>
        </div>
      </div>
    </div>
  );
}
