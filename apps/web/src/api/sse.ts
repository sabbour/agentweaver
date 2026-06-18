import { useCallback, useEffect, useRef, useState } from 'react';
import type { RunDetail } from './types';
import { AgentweaverApiClient } from './client';

export type PollStatus = 'polling' | 'done' | 'error';

interface PollState {
  run: RunDetail | null;
  status: PollStatus;
  error: string | null;
}

const TERMINAL = new Set(['completed', 'failed', 'merged', 'declined', 'merge_failed']);
const POLL_INTERVAL_MS = 2000;

// review.requested is intentionally excluded: multiple review gates occur in
// a revision cycle, so the second review.requested must not be deduplicated away.
const SINGLETON_EVENT_TYPES: ReadonlySet<string> = new Set([
  'run.completed', 'run.failed',
  'review.approved', 'review.declined',
  'merge.completed', 'merge.failed',
]);

export function useRunPoll(runId: string, apiKey: string, baseUrl: string): PollState {
  const [run, setRun] = useState<RunDetail | null>(null);
  const [status, setStatus] = useState<PollStatus>('polling');
  const [error, setError] = useState<string | null>(null);
  const stopRef = useRef(false);

  useEffect(() => {
    stopRef.current = false;
    const client = new AgentweaverApiClient(baseUrl, apiKey);

    const poll = async () => {
      while (!stopRef.current) {
        try {
          const detail = await client.getRun(runId);
          setRun(detail);
          if (TERMINAL.has(detail.status)) {
            setStatus('done');
            return;
          }
        } catch (err) {
          setStatus('error');
          setError(err instanceof Error ? err.message : String(err));
          return;
        }
        await new Promise((r) => setTimeout(r, POLL_INTERVAL_MS));
      }
    };

    void poll();
    return () => { stopRef.current = true; };
  }, [runId, apiKey, baseUrl]);

  return { run, status, error };
}

export type EventType =
  | 'agent.message.delta'
  | 'agent.message'
  | 'agent.turn.start'
  | 'agent.turn.end'
  | 'agent.message.delta'
  | 'agent.message'
  | 'agent.system_prompt'
  | 'agent.task'
  | 'agent.tools'
  | 'agent.intent'
  | 'tool.call'
  | 'tool.result'
  | 'tool.error'
  | 'tool.output'
  | 'tool.exec_result'
  | 'shell.approval_required'
  | 'tool.approval_required'
  | 'sandbox.selected'
  | 'sandbox.warning'
  | 'run.completed'
  | 'run.failed'
  | 'run.error'
  | 'run.outcome'
  | 'run.degraded'
  | 'review.requested'
  | 'review.approved'
  | 'review.declined'
  | 'review.changes_requested'
  | 'revision.started'
  | 'merge.started'
  | 'merge.completed'
  | 'merge.failed'
  | 'workflow.step'
  | 'coordinator.started'
  | 'coordinator.outcome_spec'
  | 'coordinator.outcome_spec.confirmed'
  | 'coordinator.work_plan'
  | 'coordinator.topology'
  | 'coordinator.steering'
  | 'coordinator.graph'
  | 'coordinator.children_complete'
  | 'coordinator.assembly_assembling'
  | 'coordinator.assembly_review_requested'
  | 'coordinator.assembly_complete'
  | 'coordinator.assembly_failed'
  | 'coordinator.assembly_blocked'
  | 'coordinator.assembly_declined'
  | 'subtask.dispatched'
  | 'subtask.running'
  | 'subtask.assemble_ready'
  | 'subtask.rai_flagged'
  | 'subtask.completed'
  | 'subtask.failed'
  | 'run.assemble_ready'
  | 'run.workflow_graph'
  | 'done'
  | 'error';

export interface RunStreamEvent {
  sequence: number;
  type: EventType;
  payload: Record<string, unknown>;
}

export type StreamStatus = 'connecting' | 'streaming' | 'done' | 'error';

interface StreamState {
  events: RunStreamEvent[];
  status: StreamStatus;
  error: string | null;
  reconnect: () => void;
}

export function useRunStream(runId: string, apiKey: string, baseUrl: string): StreamState {
  const [events, setEvents] = useState<RunStreamEvent[]>([]);
  const [status, setStatus] = useState<StreamStatus>('connecting');
  const [error, setError] = useState<string | null>(null);
  const [reconnectKey, setReconnectKey] = useState(0);
  const lastSeqRef = useRef(0);
  const prevRunIdRef = useRef<string>(runId);

  const reconnect = useCallback(() => {
    setReconnectKey((k) => k + 1);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;

    // On a genuine run change, clear accumulated events and reset the sequence
    // cursor so the new stream starts from the beginning. On a reconnect of the
    // same run (reconnectKey changed), keep existing events and the last known
    // sequence so the server resumes from where the previous stream ended.
    if (prevRunIdRef.current !== runId) {
      prevRunIdRef.current = runId;
      lastSeqRef.current = 0;
      setEvents([]);
    }

    // Don't attempt to connect if runId is not yet resolved.
    if (!runId) return;

    setStatus('connecting'); // eslint-disable-line react-hooks/set-state-in-effect
    setError(null);

    const connect = async () => {
      const url = `${baseUrl.replace(/\/+$/, '')}/api/runs/${encodeURIComponent(runId)}/stream`;
      try {
        const headers: Record<string, string> = {
          Authorization: `Bearer ${apiKey}`,
          Accept: 'text/event-stream',
        };
        if (lastSeqRef.current > 0) headers['Last-Event-ID'] = String(lastSeqRef.current);

        const response = await fetch(url, { headers, signal });
        if (!response.ok) throw new Error(`status ${response.status}`);
        if (!response.body) throw new Error('no body');

        setStatus('streaming');
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (!signal.aborted) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          let sep = buffer.indexOf('\n\n');
          while (sep !== -1) {
            const frame = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);

            let evtId = '';
            let evtType = '';
            let evtData = '';
            for (const line of frame.split('\n')) {
              if (line.startsWith('id:')) evtId = line.slice(3).trim();
              else if (line.startsWith('event:')) evtType = line.slice(6).trim();
              else if (line.startsWith('data:')) evtData += line.slice(5);
            }

            if (evtType === 'done') {
              setStatus('done');
              return;
            }
            if (evtType === 'error') {
              setStatus('error');
              setError('Stream error from server');
              return;
            }

            const seq = evtId ? parseInt(evtId, 10) : 0;
            if (seq > 0) lastSeqRef.current = seq;

            if (evtType && evtData) {
              let payload: Record<string, unknown> = {};
              try { payload = JSON.parse(evtData); } catch { /* ignore bad JSON */ }
              const streamEvt: RunStreamEvent = { sequence: seq, type: evtType as EventType, payload };
              setEvents((prev) => {
                if (seq > 0 && prev.some((e) => e.sequence === seq)) return prev;
                if (seq === 0 && SINGLETON_EVENT_TYPES.has(evtType) && prev.some((e) => e.type === evtType)) return prev;
                return [...prev, streamEvt];
              });
            }

            sep = buffer.indexOf('\n\n');
          }
        }
        if (!signal.aborted) setStatus('done');
      } catch (err) {
        if (signal.aborted) return;
        setStatus('error');
        setError(err instanceof Error ? err.message : String(err));
      }
    };

    void connect();
    return () => { controller.abort(); };
  }, [runId, apiKey, baseUrl, reconnectKey]);

  return { events, status, error, reconnect };
}
