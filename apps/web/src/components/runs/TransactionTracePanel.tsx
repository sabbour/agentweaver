import { useEffect, useMemo, useState } from 'react';
import { Badge, Spinner, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import type { CoordinatorChildResponse, PersistedRunEvent, RunDetail, RunTraceDto, RunTraceSpanDto } from '../../api/types';
import { apiClient } from '../../api/apiClient';

type TraceEvent = Pick<PersistedRunEvent, 'sequence' | 'type' | 'payload'>;
type TraceSource = 'appInsights' | 'eventStream';

interface EventTraceBar {
  key: string;
  runId: string;
  label: string;
  secondary: string;
  status: string;
  startedAt: number;
  endedAt: number;
  kind: 'coordinator' | 'agent';
  source: 'eventStream';
}

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

type TraceBar = EventTraceBar | AppInsightsTraceBar;

interface TraceLane {
  key: string;
  label: string;
  secondary: string;
  bars: TraceBar[];
  levels: number;
}

interface DetailState {
  loading?: boolean;
  error?: string;
  run?: RunDetail | null;
  events?: PersistedRunEvent[];
}

const START_EVENTS = new Set(['subtask.dispatched', 'subtask.running']);
const END_EVENTS = new Set(['subtask.completed', 'subtask.failed', 'subtask.assemble_ready', 'subtask.rai_flagged']);
const COORDINATOR_END_EVENTS = new Set(['coordinator.assembly_completed', 'coordinator.assembly_failed', 'coordinator.assembly_declined', 'run.completed', 'run.failed']);

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

function readTimestamp(payload: Record<string, unknown>): number | null {
  for (const key of ['timestamp_utc', 'timestampUtc', 'started_at', 'startedAt', 'ended_at', 'endedAt']) {
    const raw = payload[key];
    if (typeof raw === 'string') {
      const parsed = new Date(raw).getTime();
      if (!Number.isNaN(parsed)) return parsed;
    }
  }
  return null;
}

function readString(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'string' && value.trim() !== '') return value;
  }
  return undefined;
}

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

function summariseEvents(events: PersistedRunEvent[] | undefined, fallback?: string | null): string {
  if (events) {
    for (const event of [...events].reverse()) {
      const text = readString(event.payload, ['message', 'content', 'result', 'reason', 'detail']);
      if (text) return text;
    }
  }
  return fallback?.trim() || 'No agent output captured yet.';
}

function buildEventLanes(runId: string, events: TraceEvent[], children: CoordinatorChildResponse[]): TraceLane[] {
  const now = Date.now();
  const lanes: TraceLane[] = [];
  const coordinatorStart = events.find((event) => event.type === 'coordinator.started');
  const coordinatorEnd = [...events].reverse().find((event) => COORDINATOR_END_EVENTS.has(event.type));
  const firstTs = coordinatorStart ? readTimestamp(coordinatorStart.payload) : null;
  const endTs = coordinatorEnd ? readTimestamp(coordinatorEnd.payload) : null;
  if (firstTs != null) {
    lanes.push({
      key: `coord-${runId}`,
      label: 'Coordinator',
      secondary: 'Plan + assembly',
      levels: 1,
      bars: [{
        key: `coord-${runId}`,
        runId,
        label: 'Coordinator',
        secondary: 'Plan + assembly',
        status: coordinatorEnd?.type?.replace('coordinator.', '').replace(/\./g, ' ') ?? 'running',
        startedAt: firstTs,
        endedAt: endTs ?? now,
        kind: 'coordinator',
        source: 'eventStream',
      }],
    });
  }

  for (const child of children) {
    let startedAt: number | null = null;
    let endedAt: number | null = null;
    for (const event of events) {
      if (String(event.payload.subtaskId ?? '') !== String(child.subtaskId)
        && String(event.payload.childRunId ?? event.payload.child_run_id ?? '') !== child.childRunId) {
        continue;
      }
      const timestamp = readTimestamp(event.payload);
      if (timestamp == null) continue;
      if (START_EVENTS.has(event.type)) startedAt = startedAt == null ? timestamp : Math.min(startedAt, timestamp);
      if (END_EVENTS.has(event.type)) endedAt = endedAt == null ? timestamp : Math.max(endedAt, timestamp);
    }

    if (startedAt == null) continue;
    lanes.push({
      key: child.childRunId,
      label: child.assignedAgent,
      secondary: `Subtask ${child.subtaskId} · ${child.selectedModelId}`,
      levels: 1,
      bars: [{
        key: child.childRunId,
        runId: child.childRunId,
        label: child.assignedAgent,
        secondary: `Subtask ${child.subtaskId} · ${child.selectedModelId}`,
        status: child.childRunStatus ?? child.subtaskStatus,
        startedAt,
        endedAt: endedAt ?? now,
        kind: 'agent',
        source: 'eventStream',
      }],
    });
  }

  return lanes.sort((left, right) => left.bars[0].startedAt - right.bars[0].startedAt);
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
  events,
  children,
  title = 'Transaction trace',
  subtitle = 'Timeline of coordinator and child-agent activity for this run.',
}: {
  runId: string;
  events: TraceEvent[];
  children: CoordinatorChildResponse[];
  title?: string;
  subtitle?: string;
}) {
  const styles = useStyles();
  const eventLanes = useMemo(() => buildEventLanes(runId, events, children), [children, events, runId]);
  const [selectedBarKey, setSelectedBarKey] = useState<string | null>(null);
  const [details, setDetails] = useState<Record<string, DetailState>>({});
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
    setDetails({});
  }, [runId]);

  const appInsightsLanes = useMemo(
    () => buildAppInsightsLanes(appInsightsTrace.spans),
    [appInsightsTrace.spans],
  );
  const source: TraceSource = appInsightsLanes.length > 0 ? 'appInsights' : 'eventStream';
  const lanes = source === 'appInsights' ? appInsightsLanes : eventLanes;
  const allBars = lanes.flatMap((lane) => lane.bars);
  const selectedBar = findSelectedBar(lanes, selectedBarKey);

  const rangeStart = allBars.length > 0 ? Math.min(...allBars.map((bar) => bar.startedAt)) : 0;
  const rangeEnd = allBars.length > 0 ? Math.max(...allBars.map((bar) => bar.endedAt)) : 0;
  const rangeDuration = Math.max(1, rangeEnd - rangeStart);

  async function toggleBar(bar: TraceBar) {
    setSelectedBarKey((current) => current === bar.key ? null : bar.key);
    if (bar.source !== 'eventStream') return;
    if (details[bar.runId]?.run || details[bar.runId]?.loading) return;

    setDetails((current) => ({ ...current, [bar.runId]: { loading: true } }));
    try {
      const [run, traceEvents] = await Promise.all([
        apiClient.getRun(bar.runId),
        apiClient.getRunEvents(bar.runId).catch(() => [] as PersistedRunEvent[]),
      ]);
      setDetails((current) => ({ ...current, [bar.runId]: { run, events: traceEvents } }));
    } catch (error) {
      setDetails((current) => ({
        ...current,
        [bar.runId]: { error: error instanceof Error ? error.message : String(error) },
      }));
    }
  }

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <div>
          <Title3>{title}</Title3>
          <Text className={styles.subtitle}>{subtitle}</Text>
        </div>
        <Badge appearance="outline" size="small">
          Source: {source === 'appInsights' ? 'AppInsights' : 'Event stream'}
        </Badge>
      </div>
      {lanes.length === 0 ? (
        <Text>No trace events are available for this run yet.</Text>
      ) : (
        <div className={styles.rows}>
          {lanes.map((lane) => {
            const laneHeight = lane.levels > 1 ? lane.levels * 30 + 8 : 34;
            const isExpanded = lane.bars.some((bar) => bar.key === selectedBarKey);
            const detail = selectedBar?.source === 'eventStream' && isExpanded
              ? details[selectedBar.runId]
              : undefined;

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
                      const top = bar.source === 'appInsights' ? 4 + (bar.level * 30) : 4;
                      const label = bar.source === 'appInsights'
                        ? `${bar.label} · ${formatDuration(bar.startedAt, bar.endedAt)}`
                        : `${bar.status} · ${formatDuration(bar.startedAt, bar.endedAt)}`;
                      return (
                        <button
                          key={bar.key}
                          type="button"
                          className={`${styles.barButton} ${statusVariant(bar.status, styles)}`}
                          style={{ left: `${left}%`, width: `${width}%`, top: `${top}px` }}
                          onClick={() => { void toggleBar(bar); }}
                          title={label}
                        >
                          {label}
                        </button>
                      );
                    })}
                  </div>
                </div>
                {isExpanded && selectedBar?.source === 'appInsights' && (
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
                {isExpanded && selectedBar?.source === 'eventStream' && (
                  <div className={styles.expanded}>
                    {detail?.loading ? (
                      <Spinner size="tiny" label="Loading agent detail" />
                    ) : detail?.error ? (
                      <Text>{detail.error}</Text>
                    ) : (
                      <>
                        <div className={styles.detailMeta}>
                          <span>{detail?.run?.status ?? selectedBar.status}</span>
                          {detail?.run?.model_source && <span>{detail.run.model_source}</span>}
                          <span>{new Date(selectedBar.startedAt).toLocaleString()}</span>
                        </div>
                        <Text>{summariseEvents(detail?.events, detail?.run?.result ?? null)}</Text>
                      </>
                    )}
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
