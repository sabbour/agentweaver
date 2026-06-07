import { Text, tokens } from '@fluentui/react-components';
import type { SseEvent } from '../api/client';

interface EventListProps {
  events: SseEvent[];
}

/**
 * T064: EventList — renders all 14 event types in strict monotonic sequence order.
 * No emojis (NFR-002). Text labels and monospace content only.
 */
export function EventList({ events }: EventListProps) {
  if (events.length === 0) {
    return <Text italic style={{ color: tokens.colorNeutralForeground3 }}>No events yet.</Text>;
  }

  return (
    <div style={{ fontFamily: 'monospace', fontSize: '13px', lineHeight: '1.6' }}>
      {events.map((evt) => (
        <div
          key={evt.sequence}
          style={{
            display: 'flex',
            gap: '12px',
            padding: '4px 0',
            borderBottom: `1px solid ${tokens.colorNeutralBackground3}`,
          }}
        >
          <span
            style={{
              color: tokens.colorNeutralForeground3,
              minWidth: '40px',
              textAlign: 'right',
              flexShrink: 0,
            }}
          >
            {evt.sequence}
          </span>
          <span
            style={{
              color: getEventTypeColor(evt.eventType),
              minWidth: '160px',
              flexShrink: 0,
              fontWeight: 600,
            }}
          >
            {getEventLabel(evt.eventType)}
          </span>
          <span style={{ color: tokens.colorNeutralForeground1, wordBreak: 'break-all' }}>
            {evt.data}
          </span>
        </div>
      ))}
    </div>
  );
}

function getEventLabel(eventType: string): string {
  const labels: Record<string, string> = {
    'run.started': 'RUN STARTED',
    'run.completed': 'RUN COMPLETED',
    'run.failed': 'RUN FAILED',
    'run.bounded': 'RUN BOUNDED',
    'agent.message': 'AGENT MESSAGE',
    'tool.call': 'TOOL CALL',
    'tool.result': 'TOOL RESULT',
    'tool.rejected': 'TOOL REJECTED',
    'tool.error': 'TOOL ERROR',
    'review.requested': 'REVIEW REQUESTED',
    'review.approved': 'REVIEW APPROVED',
    'review.declined': 'REVIEW DECLINED',
    'merge.completed': 'MERGE COMPLETED',
    'merge.failed': 'MERGE FAILED',
  };
  return labels[eventType] ?? eventType.toUpperCase();
}

function getEventTypeColor(eventType: string): string {
  if (eventType.includes('failed') || eventType.includes('rejected') || eventType.includes('error')) {
    return tokens.colorPaletteRedForeground1;
  }
  if (eventType.includes('completed') || eventType.includes('approved') || eventType === 'run.started') {
    return tokens.colorPaletteGreenForeground1;
  }
  if (eventType.includes('bounded') || eventType.includes('declined')) {
    return tokens.colorPaletteDarkOrangeForeground1;
  }
  if (eventType.includes('tool')) {
    return tokens.colorBrandForeground1;
  }
  if (eventType.includes('review') || eventType.includes('merge')) {
    return tokens.colorPalettePurpleForeground2;
  }
  return tokens.colorNeutralForeground1;
}
