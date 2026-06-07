import { useEffect, useRef, useState } from 'react';
import type { RunEvent } from './types';

export type StreamStatus = 'connecting' | 'streaming' | 'done' | 'error';

interface StreamState {
  events: RunEvent[];
  status: StreamStatus;
  error: string | null;
}

// Events that end the client stream. The backend keeps the connection open
// after a run completes so a human can review; the run is only finished from a
// client's perspective once it fails, is bounded, or a review decision and any
// resulting merge have been recorded.
const TERMINAL_TYPES = new Set<string>([
  'run.failed',
  'run.bounded',
  'merge.completed',
  'merge.failed',
  'review.approved',
  'review.declined',
]);

/**
 * Streams a run's events over server-sent events using fetch so the bearer key
 * and Last-Event-ID header can be sent. Deduplicates by sequence and reconnects
 * with the last seen id until a terminal event arrives.
 */
export function useRunStream(
  runId: string,
  apiKey: string,
  baseUrl: string,
): StreamState {
  const [events, setEvents] = useState<RunEvent[]>([]);
  const [status, setStatus] = useState<StreamStatus>('connecting');
  const [error, setError] = useState<string | null>(null);
  const seenRef = useRef<Set<number>>(new Set());

  useEffect(() => {
    const controller = new AbortController();
    let stopped = false;
    let lastEventId: string | null = null;
    const trimmedBase = baseUrl.replace(/\/+$/, '');
    const url = `${trimmedBase}/api/runs/${encodeURIComponent(runId)}/stream`;

    seenRef.current = new Set();

    const handleEnvelope = (data: string) => {
      let evt: RunEvent;
      try {
        evt = JSON.parse(data) as RunEvent;
      } catch {
        return;
      }
      if (typeof evt.sequence !== 'number' || !evt.type) {
        return;
      }
      if (seenRef.current.has(evt.sequence)) {
        return;
      }
      seenRef.current.add(evt.sequence);
      setEvents((prev) => [...prev, evt]);
      if (TERMINAL_TYPES.has(evt.type)) {
        stopped = true;
        setStatus('done');
        controller.abort();
      }
    };

    const run = async () => {
      while (!stopped) {
        try {
          const headers: Record<string, string> = {
            Accept: 'text/event-stream',
            Authorization: `Bearer ${apiKey}`,
          };
          if (lastEventId !== null) {
            headers['Last-Event-ID'] = lastEventId;
          }

          const response = await fetch(url, {
            method: 'GET',
            headers,
            signal: controller.signal,
          });

          if (!response.ok) {
            const body = await response.text();
            throw new Error(`status ${response.status}: ${body}`);
          }
          if (!response.body) {
            throw new Error('response has no readable body');
          }

          setStatus('streaming');

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (!stopped) {
            const { value, done } = await reader.read();
            if (done) {
              break;
            }
            buffer += decoder.decode(value, { stream: true });

            let separator = buffer.indexOf('\n\n');
            while (separator !== -1) {
              const frame = buffer.slice(0, separator);
              buffer = buffer.slice(separator + 2);
              let data = '';
              for (const rawLine of frame.split('\n')) {
                const line = rawLine.replace(/\r$/, '');
                if (line.startsWith('id:')) {
                  lastEventId = line.slice(3).trim();
                } else if (line.startsWith('data:')) {
                  data += (data ? '\n' : '') + line.slice(5).trim();
                }
              }
              if (data) {
                handleEnvelope(data);
              }
              separator = buffer.indexOf('\n\n');
            }
          }
        } catch (err) {
          if (stopped || controller.signal.aborted) {
            return;
          }
          setStatus('error');
          setError(err instanceof Error ? err.message : String(err));
        }

        if (stopped) {
          return;
        }

        // Pause briefly before reconnecting with the last id.
        await new Promise((resolve) => setTimeout(resolve, 1000));
      }
    };

    void run();

    return () => {
      stopped = true;
      controller.abort();
    };
  }, [runId, apiKey, baseUrl]);

  return { events, status, error };
}
