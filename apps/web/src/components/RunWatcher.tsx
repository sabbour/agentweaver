import { Badge, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useRunStream, type RunStreamEvent } from '../api/sse';
import { API_KEY, API_URL } from '../config';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  header: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM },
  timeline: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  eventRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'flex-start',
    padding: tokens.spacingVerticalXS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
  },
  typeTag: { flexShrink: 0, minWidth: '140px' },
  content: { wordBreak: 'break-word', whiteSpace: 'pre-wrap', flexGrow: 1 },
});

function eventSummary(evt: RunStreamEvent): string {
  const p = evt.payload;
  switch (evt.type) {
    case 'agent.message.delta': return String(p.delta ?? '');
    case 'agent.message': return String(p.content ?? '');
    case 'agent.turn.start': return `turn ${p.turnId ?? ''}`;
    case 'agent.turn.end': return `turn ${p.turnId ?? ''} ended`;
    case 'tool.call': return `${p.toolName ?? 'tool'} ${JSON.stringify(p.arguments ?? {})}`;
    case 'tool.result': return String(p.content ?? '(empty)');
    case 'tool.error': return `${p.errorCode ?? 'error'}: ${p.errorMessage ?? ''}`;
    case 'run.completed': return String(p.summary ?? 'completed');
    case 'run.failed': return String(p.message ?? p.summary ?? 'failed');
    default: return JSON.stringify(p);
  }
}

function badgeColor(type: string): 'informative' | 'success' | 'warning' | 'danger' | 'subtle' {
  if (type.startsWith('agent.')) return 'informative';
  if (type === 'tool.call') return 'subtle';
  if (type === 'tool.result') return 'success';
  if (type === 'tool.error') return 'danger';
  if (type === 'run.completed') return 'success';
  if (type === 'run.failed') return 'danger';
  return 'subtle';
}

interface RunWatcherProps { runId: string; }

export function RunWatcher({ runId }: RunWatcherProps) {
  const styles = useStyles();
  const { events, status, error } = useRunStream(runId, API_KEY, API_URL);

  // Collapse consecutive agent.message.delta events into accumulated text
  const displayEvents: RunStreamEvent[] = [];
  let deltaAccum = '';
  let deltaMessageId: unknown = null;
  for (const evt of events) {
    if (evt.type === 'agent.message.delta') {
      deltaAccum += String(evt.payload.delta ?? '');
      deltaMessageId = evt.payload.messageId;
    } else {
      if (deltaAccum) {
        displayEvents.push({ sequence: -1, type: 'agent.message', payload: { messageId: deltaMessageId, content: deltaAccum } });
        deltaAccum = '';
        deltaMessageId = null;
      }
      displayEvents.push(evt);
    }
  }
  if (deltaAccum) {
    displayEvents.push({ sequence: -1, type: 'agent.message', payload: { messageId: deltaMessageId, content: deltaAccum } });
  }

  return (
    <div className={styles.root}>
      <div className={styles.header}>
        <Text weight="semibold">Run {runId.slice(0, 8)}</Text>
        {(status === 'connecting' || status === 'streaming') && (
          <Spinner size="tiny" label={status === 'connecting' ? 'Connecting' : 'Streaming'} />
        )}
        {status === 'done' && <Badge color="success">done</Badge>}
        {status === 'error' && <Badge color="danger">error</Badge>}
      </div>
      {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
      <div className={styles.timeline}>
        {displayEvents.map((evt, i) => (
          <div key={evt.sequence > 0 ? evt.sequence : `d-${i}`} className={styles.eventRow}>
            <Badge className={styles.typeTag} color={badgeColor(evt.type)} shape="rounded">
              {evt.type}
            </Badge>
            <Text className={styles.content}>{eventSummary(evt)}</Text>
          </div>
        ))}
        {status === 'connecting' && (
          <Text style={{ color: tokens.colorNeutralForeground3 }}>Waiting for agent...</Text>
        )}
      </div>
    </div>
  );
}
