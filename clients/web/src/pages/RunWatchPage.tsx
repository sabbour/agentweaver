import { useEffect, useRef, useState } from 'react';
import { Button, Text, tokens } from '@fluentui/react-components';
import { createRunStream, getRun, TERMINAL_EVENT_TYPES } from '../api/client';
import type { RunResponse, SseEvent } from '../api/client';
import { RunStatusBadge } from '../components/RunStatusBadge';
import { EventList } from '../components/EventList';

interface RunWatchPageProps {
  runId: string;
  onReview: (run: RunResponse) => void;
}

/**
 * T063: RunWatchPage — connects to GET /runs/{runId}/stream via EventSource.
 * Maintains events in sequence order; deduplicates by sequence.
 * Displays terminal lifecycle event and marks stream closed.
 * No emojis (NFR-002).
 */
export function RunWatchPage({ runId, onReview }: RunWatchPageProps) {
  const [events, setEvents] = useState<SseEvent[]>([]);
  const [run, setRun] = useState<RunResponse | null>(null);
  const [streamClosed, setStreamClosed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seenSequences = useRef(new Set<number>());
  const lastEventId = useRef(0);
  const eventSourceRef = useRef<EventSource | null>(null);

  useEffect(() => {
    // Fetch initial run status
    getRun(runId).then(setRun).catch(() => setError('Failed to load run.'));

    connectStream();

    return () => {
      eventSourceRef.current?.close();
    };
  }, [runId]);

  function connectStream() {
    const es = createRunStream(runId, lastEventId.current || undefined);
    eventSourceRef.current = es;

    es.onerror = () => {
      es.close();
      // Reconnect with Last-Event-ID after a short delay
      if (!streamClosed) {
        setTimeout(() => connectStream(), 2000);
      }
    };

    // Listen to all message types
    const allEventTypes = [
      'run.started', 'run.completed', 'run.failed', 'run.bounded',
      'agent.message', 'tool.call', 'tool.result', 'tool.rejected', 'tool.error',
      'review.requested', 'review.approved', 'review.declined',
      'merge.completed', 'merge.failed',
    ];

    for (const eventType of allEventTypes) {
      es.addEventListener(eventType, (e: MessageEvent) => {
        const id = Number((e as MessageEvent & { lastEventId: string }).lastEventId);
        if (seenSequences.current.has(id)) return; // deduplicate
        seenSequences.current.add(id);
        lastEventId.current = id;

        const sseEvent: SseEvent = {
          sequence: id,
          eventType,
          data: (e as MessageEvent).data,
        };

        setEvents((prev) =>
          [...prev, sseEvent].sort((a, b) => a.sequence - b.sequence),
        );

        if (TERMINAL_EVENT_TYPES.has(eventType)) {
          es.close();
          setStreamClosed(true);
          // Refresh run status after terminal event
          getRun(runId).then(setRun).catch(() => {});
        }
      });
    }
  }

  const canReview = run?.status === 'AwaitingReview';

  return (
    <div style={{ padding: '24px', maxWidth: '900px' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '12px', marginBottom: '16px' }}>
        <Text as="h1" size={700} weight="semibold">
          Run {runId}
        </Text>
        {run && <RunStatusBadge status={run.status} />}
        {streamClosed && (
          <Text style={{ color: tokens.colorNeutralForeground3 }}>(stream closed)</Text>
        )}
      </div>

      {run && (
        <div
          style={{
            marginBottom: '16px',
            padding: '12px',
            background: tokens.colorNeutralBackground2,
            borderRadius: tokens.borderRadiusMedium,
            fontSize: '13px',
          }}
        >
          <div><strong>Branch:</strong> {run.originatingBranch}</div>
          <div><strong>Model:</strong> {run.modelSource}</div>
          <div><strong>Submitted by:</strong> {run.submittedBy}</div>
          {run.failureReason && (
            <div style={{ color: tokens.colorPaletteRedForeground1 }}>
              <strong>Failure:</strong> {run.failureReason}
            </div>
          )}
        </div>
      )}

      {error && (
        <Text style={{ color: tokens.colorPaletteRedForeground1, marginBottom: '8px' }}>
          {error}
        </Text>
      )}

      <div style={{ marginBottom: '16px' }}>
        <EventList events={events} />
      </div>

      {canReview && run && (
        <Button appearance="primary" onClick={() => onReview(run)}>
          Review Run
        </Button>
      )}
    </div>
  );
}
