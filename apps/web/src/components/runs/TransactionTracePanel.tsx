import { useEffect, useMemo, useState } from 'react';
import { Badge, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import type { RunTraceDto, RunTraceSpanDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';

interface AppInsightsTraceBar {
  key: string;
  runId: string;
  label: string;
  status: string;
  startedAt: number;
  endedAt: number;
  source: 'appInsights';
  span: RunTraceSpanDto;
  level: number;
}

type TraceBar = AppInsightsTraceBar;

interface TraceLane {
  key: string;
  label: string;
  secondary: string;
  bars: TraceBar[];
  levels: number;
}

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
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  rows: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  row: {
    display: 'grid',
    gridTemplateColumns: '180px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalM,
    alignItems: 'start',
  },
  rowMeta: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  rowLabel: {
    fontWeight: tokens.fontWeightSemibold,
  },
  rowSecondary: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  lane: {
    position: 'relative',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  barButton: {
    position: 'absolute',
    height: '26px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorBrandBackground,
    padding: `0 ${tokens.spacingHorizontalS}`,
    cursor: 'pointer',
    textAlign: 'left',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  barDanger: {
    backgroundColor: tokens.colorPaletteRedBackground3,
    color: tokens.colorNeutralForeground1,
  },
  barWarning: {
    backgroundColor: tokens.colorPaletteYellowBackground3,
    color: tokens.colorNeutralForeground1,
  },
  barSuccess: {
    backgroundColor: tokens.colorPaletteGreenBackground3,
    color: tokens.colorNeutralForeground1,
  },
  expanded: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  detailMeta: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  detailGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
});

function formatDuration(startedAt: number, endedAt: number): string {
  return formatDurationMs(Math.max(0, endedAt - startedAt));
}

function formatDurationMs(durationMs: number): string {
  const seconds = Math.max(0, durationMs / 1000);
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(1)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
}

function formatNumber(value: number | null | undefined): string {
  return value == null ? '—' : value.toLocaleString();
}

function statusVariant(status: string, styles: ReturnType<typeof useStyles>) {
  if (/(failed|declined|blocked|error)/i.test(status)) return styles.barDanger;
  if (/(review|assembly|flagged|awaiting)/i.test(status)) return styles.barWarning;
  if (/(complete|completed|merged|ready|success|200)/i.test(status)) return styles.barSuccess;
  return '';
}


function buildAppInsightsLanes(spans: RunTraceSpanDto[]): TraceLane[] {
  if (!spans.length) return [];

  const lanes = new Map<string, AppInsightsTraceBar[]>();
  for (const span of spans) {
    const startedAt = new Date(span.timestamp).getTime();
    if (Number.isNaN(startedAt)) continue;
    const durationMs = Number.isFinite(span.durationMs) ? span.durationMs : 0;
    const endedAt = startedAt + Math.max(1, durationMs);
    const laneKey = span.agentName?.trim() || 'Unknown agent';
    const status = span.success ? 'success' : (span.resultCode?.trim() || 'failed');
    const bar: AppInsightsTraceBar = {
      key: `${span.id}-${startedAt}`,
      runId: span.id,
      label: span.name,
      status,
      startedAt,
      endedAt,
      source: 'appInsights',
      span,
      level: 0,
    };
    lanes.set(laneKey, [...(lanes.get(laneKey) ?? []), bar]);
  }

  return [...lanes.entries()]
    .map(([label, laneBars]) => {
      const sortedBars = [...laneBars].sort((left, right) => left.startedAt - right.startedAt);
      const levelEnds: number[] = [];
      for (const bar of sortedBars) {
        let level = levelEnds.findIndex((endAt) => bar.startedAt >= endAt);
        if (level === -1) {
          level = levelEnds.length;
          levelEnds.push(bar.endedAt);
        } else {
          levelEnds[level] = bar.endedAt;
        }
        bar.level = level;
      }

      return {
        key: label,
        label,
        secondary: `${sortedBars.length} AppInsights span${sortedBars.length === 1 ? '' : 's'}`,
        bars: sortedBars,
        levels: Math.max(1, levelEnds.length),
      };
    })
    .sort((left, right) => left.bars[0].startedAt - right.bars[0].startedAt);
}

function findSelectedBar(lanes: TraceLane[], key: string | null): TraceBar | null {
  if (!key) return null;
  for (const lane of lanes) {
    const bar = lane.bars.find((candidate) => candidate.key === key);
    if (bar) return bar;
  }
  return null;
}

export function TransactionTracePanel({
  runId,
  title = 'Transaction trace',
  subtitle = 'Timeline of agent activity from AppInsights distributed traces.',
}: {
  runId: string;
  title?: string;
  subtitle?: string;
}) {
  const styles = useStyles();
  const [selectedBarKey, setSelectedBarKey] = useState<string | null>(null);
  const [appInsightsTrace, setAppInsightsTrace] = useState<RunTraceDto>({ runId, spans: [] });

  useEffect(() => {
    let cancelled = false;
    setAppInsightsTrace({ runId, spans: [] });
    void apiClient.getRunTraces(runId)
      .then((trace) => {
        if (!cancelled) setAppInsightsTrace(trace);
      })
      .catch(() => {
        if (!cancelled) setAppInsightsTrace({ runId, spans: [] });
      });
    return () => { cancelled = true; };
  }, [runId]);

  useEffect(() => {
    setSelectedBarKey(null);
  }, [runId]);

  const lanes = useMemo(
    () => buildAppInsightsLanes(appInsightsTrace.spans),
    [appInsightsTrace.spans],
  );
  const allBars = lanes.flatMap((lane) => lane.bars);
  const selectedBar = findSelectedBar(lanes, selectedBarKey);

  const rangeStart = allBars.length > 0 ? Math.min(...allBars.map((bar) => bar.startedAt)) : 0;
  const rangeEnd = allBars.length > 0 ? Math.max(...allBars.map((bar) => bar.endedAt)) : 0;
  const rangeDuration = Math.max(1, rangeEnd - rangeStart);

  function toggleBar(bar: TraceBar) {
    setSelectedBarKey((current) => current === bar.key ? null : bar.key);
  }

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <div>
          <Title3>{title}</Title3>
          <Text className={styles.subtitle}>{subtitle}</Text>
        </div>
        <Badge appearance="outline" size="small">AppInsights</Badge>
      </div>
      {lanes.length === 0 ? (
        <Text>No AppInsights trace data available for this run yet.</Text>
      ) : (
        <div className={styles.rows}>
          {lanes.map((lane) => {
            const laneHeight = lane.levels > 1 ? lane.levels * 30 + 8 : 34;
            const isExpanded = lane.bars.some((bar) => bar.key === selectedBarKey);

            return (
              <div key={lane.key}>
                <div className={styles.row}>
                  <div className={styles.rowMeta}>
                    <Text className={styles.rowLabel}>{lane.label}</Text>
                    <Text className={styles.rowSecondary}>{lane.secondary}</Text>
                  </div>
                  <div className={styles.lane} style={{ height: `${laneHeight}px` }}>
                    {lane.bars.map((bar) => {
                      const left = ((bar.startedAt - rangeStart) / rangeDuration) * 100;
                      const width = Math.max(8, ((bar.endedAt - bar.startedAt) / rangeDuration) * 100);
                      const top = 4 + (bar.level * 30);
                      const label = `${bar.label} · ${formatDuration(bar.startedAt, bar.endedAt)}`;
                      return (
                        <button
                          key={bar.key}
                          type="button"
                          className={`${styles.barButton} ${statusVariant(bar.status, styles)}`}
                          style={{ left: `${left}%`, width: `${width}%`, top: `${top}px` }}
                          onClick={() => { toggleBar(bar); }}
                          title={label}
                        >
                          {label}
                        </button>
                      );
                    })}
                  </div>
                </div>
                {isExpanded && selectedBar && (
                  <div className={styles.expanded}>
                    <div className={styles.detailMeta}>
                      <span>{new Date(selectedBar.span.timestamp).toLocaleString()}</span>
                      <span>{selectedBar.span.success ? 'success' : selectedBar.status}</span>
                      {selectedBar.span.resultCode && <span>Result {selectedBar.span.resultCode}</span>}
                    </div>
                    <Text>{selectedBar.span.name}</Text>
                    <div className={styles.detailGrid}>
                      <Text>Model: {selectedBar.span.model ?? '—'}</Text>
                      <Text>Input tokens: {formatNumber(selectedBar.span.inputTokens)}</Text>
                      <Text>Output tokens: {formatNumber(selectedBar.span.outputTokens)}</Text>
                      <Text>Duration: {formatDurationMs(selectedBar.span.durationMs)}</Text>
                      <Text>Operation: {selectedBar.span.operationName ?? '—'}</Text>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
