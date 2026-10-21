/**
 * Shared 0.x metric sections for one project: the Activity header with the
 * range selector, the runs-over-time chart, run totals, and the model and
 * agent usage tables. The Dashboard and the Overview both render these.
 */
import {
  Badge,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import type { ReactNode } from 'react';
import type { ProjectMetricsDay, ProjectUsageItem } from './contracts';
import { errorCode } from './errors';
import { ErrorNotice } from './pageParts';
import {
  METRICS_RANGES,
  NOT_MEASURED,
  formatCost,
  formatCount,
  formatDuration,
  metricsErrorMessage,
  metricsRangeLabel,
  sortUsage,
  type MetricsRangeKey,
  type ProjectMetricsData,
} from './projectMetrics';

// Line colors from the 0.x Dashboard chart.
const CREATED_COLOR = '#635c57';
const COMPLETED_COLOR = tokens.colorPaletteGreenForeground1;

const useStyles = makeStyles({
  header: { display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
  headingGroup: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  sectionTitle: { fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold },
  subtitle: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
  controls: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
  filterGroup: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  mainGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
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
    boxShadow: tokens.shadow2,
    minWidth: 0,
  },
  panelHeader: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, flexWrap: 'wrap' },
  panelTitle: { display: 'block', fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400, fontWeight: tokens.fontWeightSemibold },
  legend: { display: 'flex', gap: tokens.spacingHorizontalL, alignItems: 'center', flexWrap: 'wrap', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 },
  legendItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
  swatch: { width: '12px', height: '12px', borderRadius: tokens.borderRadiusSmall, display: 'inline-block' },
  signalPanel: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 620px)': { gridTemplateColumns: '1fr' },
  },
  signalItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
    minWidth: 0,
  },
  signalLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  signalValue: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  empty: { color: tokens.colorNeutralForeground3 },
  usageBody: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL, minWidth: 0 },
  tableWrap: { overflowX: 'auto', minWidth: 0 },
  // Auto layout sizes each column to its content instead of equal fixed
  // columns, so names and costs fit. A wide table scrolls sideways in tableWrap.
  table: { minWidth: '100%', width: 'max-content', tableLayout: 'auto' },
  headerCell: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2, whiteSpace: 'nowrap' },
  // Names never break inside a word; the name column gets room to fit them.
  nameColumn: { minWidth: '200px' },
  nameCell: { fontWeight: tokens.fontWeightSemibold, wordBreak: 'normal', overflowWrap: 'normal', whiteSpace: 'nowrap' },
  idCaption: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, wordBreak: 'normal', overflowWrap: 'normal', whiteSpace: 'nowrap' },
  number: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  // One cost subtotal per line, and each line never wraps.
  costList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, margin: 0, padding: 0, listStyle: 'none', whiteSpace: 'nowrap' },
});

/** 0.x range selector: last 7, 30, or 90 days. */
export function MetricsRangeSelect({ value, onChange, label = 'Time range' }: {
  value: MetricsRangeKey;
  onChange: (value: MetricsRangeKey) => void;
  label?: string;
}) {
  const styles = useStyles();
  return (
    <div className={styles.filterGroup}>
      <Text>Range</Text>
      <Select value={value} onChange={(_event, data) => onChange(data.value as MetricsRangeKey)} aria-label={label} size="small" style={{ width: '140px' }}>
        {METRICS_RANGES.map((range) => <option key={range.key} value={range.key}>{range.label}</option>)}
      </Select>
    </div>
  );
}

/**
 * Line chart of created and completed runs per UTC day, drawn as SVG like the
 * 0.x chart. Days are sorted by date first, because the chart reads left to right.
 */
export function ActivityChart({ series }: { series: readonly ProjectMetricsDay[] }) {
  const points = [...series].sort((a, b) => a.date.localeCompare(b.date));
  const W = 720;
  const H = 220;
  const pad = { top: 16, right: 16, bottom: 28, left: 32 };
  const innerW = W - pad.left - pad.right;
  const innerH = H - pad.top - pad.bottom;
  // Keep the top of the scale at 1 or more so an all-zero range does not divide by zero.
  const maxValue = Math.max(1, ...points.map((point) => Math.max(point.created, point.completed)));
  const n = points.length;
  // One point sits on the left edge; more points spread across the width.
  const x = (index: number) => pad.left + (n <= 1 ? 0 : (index / (n - 1)) * innerW);
  const y = (value: number) => pad.top + innerH - (value / maxValue) * innerH;
  const toPath = (select: (point: ProjectMetricsDay) => number) =>
    points.map((point, index) => `${index === 0 ? 'M' : 'L'}${x(index).toFixed(1)},${y(select(point)).toFixed(1)}`).join(' ');
  const created = points.reduce((sum, point) => sum + point.created, 0);
  const completed = points.reduce((sum, point) => sum + point.completed, 0);
  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      width="100%"
      height={H}
      role="img"
      aria-label={`Created and completed runs per day: ${created} created and ${completed} completed from ${points[0]?.date ?? ''} to ${points[n - 1]?.date ?? ''}.`}
    >
      <line x1={pad.left} y1={pad.top + innerH} x2={pad.left + innerW} y2={pad.top + innerH} stroke={tokens.colorNeutralStroke2} strokeWidth={1} />
      <line x1={pad.left} y1={pad.top} x2={pad.left + innerW} y2={pad.top} stroke={tokens.colorNeutralStroke3} strokeWidth={1} />
      <text x={pad.left} y={pad.top + innerH + 18} fontSize={10} fill={tokens.colorNeutralForeground3}>{points[0]?.date ?? ''}</text>
      <text x={pad.left + innerW} y={pad.top + innerH + 18} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">{points[n - 1]?.date ?? ''}</text>
      <text x={pad.left - 6} y={pad.top + 8} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">{maxValue}</text>
      <path d={toPath((point) => point.created)} fill="none" stroke={CREATED_COLOR} strokeWidth={2.5} />
      <path d={toPath((point) => point.completed)} fill="none" stroke={COMPLETED_COLOR} strokeWidth={2.5} />
      {/* A single day has no line to draw, so mark it with dots. */}
      {n === 1 && (
        <>
          <circle cx={x(0)} cy={y(points[0].created)} r={4} fill={CREATED_COLOR} />
          <circle cx={x(0)} cy={y(points[0].completed)} r={4} fill={COMPLETED_COLOR} />
        </>
      )}
    </svg>
  );
}

/** Cost subtotals as reported, one line per price unit and disposition. */
function CostCell({ cost }: { cost: ProjectUsageItem['cost'] }) {
  const styles = useStyles();
  if (cost.length === 0) return <>{NOT_MEASURED}</>;
  return (
    <ul className={styles.costList}>
      {cost.map((entry, index) => <li key={`${entry.priceUnit ?? ''}-${entry.disposition}-${index}`}>{formatCost(entry)}</li>)}
    </ul>
  );
}

/**
 * Usage table for models or agents. `nameFor` maps an ID to a display name;
 * when it returns a different name, the ID is shown under it.
 */
export function UsageTable({ title, kind, items, nameFor }: {
  title: string;
  kind: 'Model' | 'Agent';
  items: readonly ProjectUsageItem[];
  nameFor?: (id: string) => string | undefined;
}) {
  const styles = useStyles();
  const rows = sortUsage(items);
  return (
    <section className={styles.panel} aria-label={title}>
      <Text as="h3" className={styles.panelTitle}>{title}</Text>
      {rows.length === 0 ? <Text className={styles.empty}>No {kind.toLowerCase()} usage in this range.</Text> : (
        <div className={styles.tableWrap}>
          <Table aria-label={title} size="small" className={styles.table}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell className={mergeClasses(styles.headerCell, styles.nameColumn)}>{kind}</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Runs</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Requests</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Input tokens</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Output tokens</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Cached tokens</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Cache write tokens</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Reasoning tokens</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Average duration</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Cost</TableHeaderCell>
                <TableHeaderCell className={styles.headerCell}>Unpriced requests</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((row) => {
                const name = nameFor?.(row.id);
                return (
                  <TableRow key={row.id}>
                    <TableCell className={styles.nameColumn}>
                      <Text className={styles.nameCell}>{name ?? row.id}</Text>
                      {name && name !== row.id && <Text className={styles.idCaption}>{row.id}</Text>}
                    </TableCell>
                    <TableCell className={styles.number}>{formatCount(row.runs)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.requestCount)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.inputTokens)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.outputTokens)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.cachedTokens)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.cacheWriteTokens)}</TableCell>
                    <TableCell className={styles.number}>{formatCount(row.reasoningTokens)}</TableCell>
                    <TableCell className={styles.number}>{formatDuration(row.avgDurationMs)}</TableCell>
                    <TableCell><CostCell cost={row.cost} /></TableCell>
                    <TableCell className={styles.number}>{formatCount(row.unpricedRequestCount)}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
      )}
    </section>
  );
}

/**
 * Renders the Activity header, the runs-over-time chart, run totals, and the
 * usage tables for one project. `controls` adds page controls, such as a
 * project picker, next to the range selector.
 */
export function ProjectMetricsSections({ data, range, onRangeChange, agentNames, controls }: {
  data: ProjectMetricsData;
  range: MetricsRangeKey;
  onRangeChange: (value: MetricsRangeKey) => void;
  agentNames?: ReadonlyMap<string, string>;
  controls?: ReactNode;
}) {
  const styles = useStyles();
  const label = metricsRangeLabel(range);
  const { metrics, usage } = data;
  const series = metrics?.series ?? [];
  const created = series.reduce((sum, point) => sum + point.created, 0);
  const completed = series.reduce((sum, point) => sum + point.completed, 0);
  // Treat a range with no created and no completed runs as empty, so the
  // chart is not a flat line at zero.
  const hasActivity = created > 0 || completed > 0;
  return (
    <>
      <div className={styles.header}>
        <div className={styles.headingGroup}>
          <Text as="h2" className={styles.sectionTitle}>Activity</Text>
          <Text className={styles.subtitle}>Select a range to compare created and completed runs.</Text>
        </div>
        <div className={styles.controls}>
          {controls}
          {data.loading && (metrics || usage) && <Badge appearance="outline">Refreshing</Badge>}
          <MetricsRangeSelect value={range} onChange={onRangeChange} />
        </div>
      </div>
      {data.metricsError != null && <ErrorNotice code={errorCode(data.metricsError)}>{metricsErrorMessage(data.metricsError, 'run metrics')}</ErrorNotice>}
      <div className={styles.mainGrid}>
        <section className={styles.panel} aria-labelledby="runs-over-time-title">
          <div className={styles.panelHeader}>
            <div className={styles.headingGroup}>
              <Text as="h3" id="runs-over-time-title" className={styles.panelTitle}>Runs over time</Text>
              <Text className={styles.subtitle}>Created and completed runs per UTC day during the {label}.</Text>
            </div>
            <div className={styles.legend} aria-label="Run activity legend">
              <span className={styles.legendItem}><span className={styles.swatch} style={{ backgroundColor: CREATED_COLOR }} />Created</span>
              <span className={styles.legendItem}><span className={styles.swatch} style={{ backgroundColor: COMPLETED_COLOR }} />Completed</span>
            </div>
          </div>
          {metrics ? (hasActivity ? <ActivityChart series={series} /> : <Text className={styles.empty}>No runs were created or completed in this range.</Text>)
            : data.loading ? <Spinner size="small" label="Loading run metrics" />
              : <Text className={styles.empty}>Run metrics are not available.</Text>}
        </section>
        <aside className={styles.panel} aria-labelledby="run-totals-title">
          <div className={styles.headingGroup}>
            <Text as="h3" id="run-totals-title" className={styles.panelTitle}>Run totals</Text>
            <Text className={styles.subtitle}>Run totals for the {label}.</Text>
          </div>
          <div className={styles.signalPanel}>
            <div className={styles.signalItem}>
              <Text className={styles.signalLabel}>Created</Text>
              <Text className={styles.signalValue}>{metrics ? created.toLocaleString() : NOT_MEASURED}</Text>
            </div>
            <div className={styles.signalItem}>
              <Text className={styles.signalLabel}>Completed</Text>
              <Text className={styles.signalValue}>{metrics ? completed.toLocaleString() : NOT_MEASURED}</Text>
            </div>
          </div>
        </aside>
      </div>
      <section className={styles.panel} aria-labelledby="usage-title">
        <div className={styles.headingGroup}>
          <Text as="h2" id="usage-title" className={styles.sectionTitle}>Model and agent usage</Text>
          <Text className={styles.subtitle}>
            Requests, tokens, duration, and cost reported by runs during the {label}. Costs are shown as reported, per unit; {NOT_MEASURED} means not measured.
          </Text>
        </div>
        {data.usageError != null && <ErrorNotice code={errorCode(data.usageError)}>{metricsErrorMessage(data.usageError, 'usage')}</ErrorNotice>}
        {usage ? (
          <div className={styles.usageBody}>
            <UsageTable title="Usage by model" kind="Model" items={usage.usageByModel} />
            <UsageTable title="Usage by agent" kind="Agent" items={usage.usageByAgent} nameFor={(id) => agentNames?.get(id)} />
          </div>
        ) : data.loading ? <Spinner size="small" label="Loading usage" />
          : data.usageError == null ? null : <Text className={styles.empty}>Usage is not available.</Text>}
      </section>
    </>
  );
}
