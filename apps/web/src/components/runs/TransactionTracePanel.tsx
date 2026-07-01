import { useMemo, useState } from 'react';
import { Spinner, Text, Title3, makeStyles, tokens } from '@fluentui/react-components';
import type { CoordinatorChildResponse, PersistedRunEvent, RunDetail } from '../../api/types';
import { apiClient } from '../../api/apiClient';

type TraceEvent = Pick<PersistedRunEvent, 'sequence' | 'type' | 'payload'>;

interface TraceRow {
  key: string;
  runId: string;
  label: string;
  secondary: string;
  status: string;
  startedAt: number;
  endedAt: number;
  kind: 'coordinator' | 'agent';
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
    alignItems: 'center',
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
    height: '34px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
  },
  barButton: {
    position: 'absolute',
    top: '4px',
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
  const seconds = Math.max(0, (endedAt - startedAt) / 1000);
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(1)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
}

function statusVariant(status: string, styles: ReturnType<typeof useStyles>) {
  if (/(failed|declined|blocked|error)/i.test(status)) return styles.barDanger;
  if (/(review|assembly|flagged|awaiting)/i.test(status)) return styles.barWarning;
  if (/(complete|completed|merged|ready)/i.test(status)) return styles.barSuccess;
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

function buildRows(runId: string, events: TraceEvent[], children: CoordinatorChildResponse[]): TraceRow[] {
  const now = Date.now();
  const rows: TraceRow[] = [];
  const coordinatorStart = events.find((event) => event.type === 'coordinator.started');
  const coordinatorEnd = [...events].reverse().find((event) => COORDINATOR_END_EVENTS.has(event.type));
  const firstTs = coordinatorStart ? readTimestamp(coordinatorStart.payload) : null;
  const endTs = coordinatorEnd ? readTimestamp(coordinatorEnd.payload) : null;
  if (firstTs != null) {
    rows.push({
      key: `coord-${runId}`,
      runId,
      label: 'Coordinator',
      secondary: 'Plan + assembly',
      status: coordinatorEnd?.type?.replace('coordinator.', '').replace(/\./g, ' ') ?? 'running',
      startedAt: firstTs,
      endedAt: endTs ?? now,
      kind: 'coordinator',
    });
  }

  for (const child of children) {
    let startedAt: number | null = null;
    let endedAt: number | null = null;
    for (const event of events) {
      if (String(event.payload.subtaskId ?? '') !== String(child.subtaskId) && String(event.payload.childRunId ?? event.payload.child_run_id ?? '') !== child.childRunId) {
        continue;
      }
      const timestamp = readTimestamp(event.payload);
      if (timestamp == null) continue;
      if (START_EVENTS.has(event.type)) startedAt = startedAt == null ? timestamp : Math.min(startedAt, timestamp);
      if (END_EVENTS.has(event.type)) endedAt = endedAt == null ? timestamp : Math.max(endedAt, timestamp);
    }

    if (startedAt == null) continue;
    rows.push({
      key: child.childRunId,
      runId: child.childRunId,
      label: child.assignedAgent,
      secondary: `Subtask ${child.subtaskId} · ${child.selectedModelId}`,
      status: child.childRunStatus ?? child.subtaskStatus,
      startedAt,
      endedAt: endedAt ?? now,
      kind: 'agent',
    });
  }

  return rows.sort((left, right) => left.startedAt - right.startedAt);
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
  const rows = useMemo(() => buildRows(runId, events, children), [children, events, runId]);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [details, setDetails] = useState<Record<string, DetailState>>({});

  const rangeStart = rows.length > 0 ? Math.min(...rows.map((row) => row.startedAt)) : 0;
  const rangeEnd = rows.length > 0 ? Math.max(...rows.map((row) => row.endedAt)) : 0;
  const rangeDuration = Math.max(1, rangeEnd - rangeStart);

  async function toggleRow(targetRunId: string) {
    setExpandedId((current) => (current === targetRunId ? null : targetRunId));
    if (details[targetRunId]?.run || details[targetRunId]?.loading) return;

    setDetails((current) => ({ ...current, [targetRunId]: { loading: true } }));
    try {
      const [run, traceEvents] = await Promise.all([
        apiClient.getRun(targetRunId),
        apiClient.getRunEvents(targetRunId).catch(() => [] as PersistedRunEvent[]),
      ]);
      setDetails((current) => ({ ...current, [targetRunId]: { run, events: traceEvents } }));
    } catch (error) {
      setDetails((current) => ({
        ...current,
        [targetRunId]: { error: error instanceof Error ? error.message : String(error) },
      }));
    }
  }

  return (
    <div className={styles.panel}>
      <div>
        <Title3>{title}</Title3>
        <Text className={styles.subtitle}>{subtitle}</Text>
      </div>
      {rows.length === 0 ? (
        <Text>No trace events are available for this run yet.</Text>
      ) : (
        <div className={styles.rows}>
          {rows.map((row) => {
            const left = ((row.startedAt - rangeStart) / rangeDuration) * 100;
            const width = Math.max(8, ((row.endedAt - row.startedAt) / rangeDuration) * 100);
            const detail = details[row.runId];
            const isExpanded = expandedId === row.runId;
            return (
              <div key={row.key}>
                <div className={styles.row}>
                  <div className={styles.rowMeta}>
                    <Text className={styles.rowLabel}>{row.label}</Text>
                    <Text className={styles.rowSecondary}>{row.secondary}</Text>
                  </div>
                  <div className={styles.lane}>
                    <button
                      type="button"
                      className={`${styles.barButton} ${statusVariant(row.status, styles)}`}
                      style={{ left: `${left}%`, width: `${width}%` }}
                      onClick={() => { void toggleRow(row.runId); }}
                    >
                      {row.status} · {formatDuration(row.startedAt, row.endedAt)}
                    </button>
                  </div>
                </div>
                {isExpanded && (
                  <div className={styles.expanded}>
                    {detail?.loading ? (
                      <Spinner size="tiny" label="Loading agent detail" />
                    ) : detail?.error ? (
                      <Text>{detail.error}</Text>
                    ) : (
                      <>
                        <div className={styles.detailMeta}>
                          <span>{detail?.run?.status ?? row.status}</span>
                          {detail?.run?.model_source && <span>{detail.run.model_source}</span>}
                          <span>{new Date(row.startedAt).toLocaleString()}</span>
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
