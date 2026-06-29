import { useCallback, useEffect, useRef, useState } from 'react';
import type { RunDetail } from './types';
import { AgentweaverApiClient } from './client';
import { API_URL, getSessionToken } from '../config';

export type PollStatus = 'polling' | 'done' | 'error';

interface PollState {
  run: RunDetail | null;
  status: PollStatus;
  error: string | null;
}

const TERMINAL = new Set(['completed', 'failed', 'merged', 'declined', 'merge_failed']);
const POLL_INTERVAL_MS = 2000;
const TERMINAL_EVENT_TYPES = new Set([
  'run.completed',
  'run.failed',
  'merge.completed',
  'merge.failed',
  'coordinator.assembly_completed',
  'coordinator.assembly_failed',
  'coordinator.assembly_declined',
]);
const RECONNECT_DELAYS_MS = [1000, 2000, 4000, 8000, 16000, 30000];
const MAX_CONSECUTIVE_FAILURES = 5;
const DEFAULT_EVENT_BUFFER_LIMIT = 1000;

// review.requested is intentionally excluded: multiple review gates occur in
// a revision cycle, so the second review.requested must not be deduplicated away.
const SINGLETON_EVENT_TYPES: ReadonlySet<string> = new Set([
  'run.completed', 'run.failed',
  'review.approved', 'review.declined',
  'merge.completed', 'merge.failed',
]);

export function useRunPoll(runId: string, baseUrl: string = API_URL): PollState {
  const [run, setRun] = useState<RunDetail | null>(null);
  const [status, setStatus] = useState<PollStatus>('polling');
  const [error, setError] = useState<string | null>(null);
  const stopRef = useRef(false);

  useEffect(() => {
    stopRef.current = false;
    const client = new AgentweaverApiClient(baseUrl);

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
  }, [runId, baseUrl]);

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
  | 'coordinator.recovered'
  | 'coordinator.outcome_spec'
  | 'coordinator.outcome_spec.confirmed'
  | 'coordinator.work_plan'
  | 'coordinator.topology'
  | 'coordinator.steering'
  | 'coordinator.graph'
  | 'coordinator.children_complete'
  | 'coordinator.assembly_started'
  | 'coordinator.assembly_rai_started'
  | 'coordinator.assembly_rai_completed'
  | 'coordinator.assembly_review_requested'
  | 'coordinator.assembly_review_approved'
  | 'coordinator.assembly_changes_requested'
  | 'coordinator.assembly_merge_started'
  | 'coordinator.assembly_merge_completed'
  | 'coordinator.assembly_merge_failed'
  | 'coordinator.assembly_scribe_started'
  | 'coordinator.assembly_scribe_completed'
  | 'coordinator.assembly_completed'
  | 'coordinator.assembly_failed'
  | 'coordinator.assembly_blocked'
  | 'coordinator.assembly_declined'
  | 'subtask.dispatched'
  | 'subtask.running'
  | 'subtask.assemble_ready'
  | 'subtask.rai_flagged'
  | 'subtask.completed'
  | 'subtask.failed'
  | 'subtask.pending_capacity'
  | 'agent.question_asked'
  | 'agent.question_answered'
  | 'tool.auto_approved'
  | 'coordinator.child_question'
  | 'coordinator.child_approval_required'
  | 'coordinator.autopilot_answered'
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
  droppedEventCount: number;
  status: StreamStatus;
  error: string | null;
  reconnect: () => void;
}

export function useRunStream(runId: string, baseUrl: string = API_URL, maxEvents = DEFAULT_EVENT_BUFFER_LIMIT): StreamState {
  const [events, setEvents] = useState<RunStreamEvent[]>([]);
  const [droppedEventCount, setDroppedEventCount] = useState(0);
  const [status, setStatus] = useState<StreamStatus>('connecting');
  const [error, setError] = useState<string | null>(null);
  const [reconnectKey, setReconnectKey] = useState(0);
  const lastSeqRef = useRef(0);
  const terminalRef = useRef(false);
  const prevRunIdRef = useRef<string>(runId);

  const reconnect = useCallback(() => {
    setReconnectKey((k) => k + 1);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;

    if (prevRunIdRef.current !== runId) {
      prevRunIdRef.current = runId;
      lastSeqRef.current = 0;
      terminalRef.current = false;
      setEvents([]);
      setDroppedEventCount(0);
    }

    if (!runId) return;

    setStatus('connecting'); // eslint-disable-line react-hooks/set-state-in-effect
    setError(null);

    const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

    const appendEvent = (streamEvt: RunStreamEvent) => {
      setEvents((prev) => {
        if (streamEvt.sequence === 0 && SINGLETON_EVENT_TYPES.has(streamEvt.type) && prev.some((e) => e.type === streamEvt.type)) {
          return prev;
        }
        const next = [...prev, streamEvt];
        if (next.length <= maxEvents) return next;
        const overflow = next.length - maxEvents;
        setDroppedEventCount((count) => count + overflow);
        return next.slice(overflow);
      });
    };

    const connectOnce = async (): Promise<boolean> => {
      const url = `${baseUrl.replace(/\/+$/, '')}/runs/${encodeURIComponent(runId)}/stream`;
      const token = getSessionToken();
      const headers: Record<string, string> = { Accept: 'text/event-stream' };
      if (token) headers.Authorization = `Bearer ${token}`;
      if (lastSeqRef.current > 0) headers['Last-Event-ID'] = String(lastSeqRef.current);

      const response = await fetch(url, { headers, signal, credentials: 'include' });
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
            terminalRef.current = true;
            setStatus('done');
            return true;
          }
          if (evtType === 'error') throw new Error('Stream error from server');

          const seq = evtId ? parseInt(evtId, 10) : 0;
          if (seq > 0 && seq <= lastSeqRef.current) {
            sep = buffer.indexOf('\n\n');
            continue;
          }

          if (evtType && evtData) {
            let payload: Record<string, unknown> = {};
            try { payload = JSON.parse(evtData); } catch { /* ignore bad JSON */ }
            const streamEvt: RunStreamEvent = { sequence: seq, type: evtType as EventType, payload };
            appendEvent(streamEvt);
            if (seq > 0) lastSeqRef.current = seq;
            if (TERMINAL_EVENT_TYPES.has(evtType)) terminalRef.current = true;
          }

          sep = buffer.indexOf('\n\n');
        }
      }

      return terminalRef.current;
    };

    const connect = async () => {
      let consecutiveFailures = 0;
      while (!signal.aborted && !terminalRef.current) {
        try {
          const finished = await connectOnce();
          if (signal.aborted || finished || terminalRef.current) return;
          throw new Error('Stream closed');
        } catch (err) {
          if (signal.aborted || terminalRef.current) return;
          consecutiveFailures += 1;
          if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
            setStatus('error');
            setError(err instanceof Error ? err.message : String(err));
            return;
          }
          const delay = RECONNECT_DELAYS_MS[Math.min(consecutiveFailures - 1, RECONNECT_DELAYS_MS.length - 1)];
          setStatus('connecting');
          setError(`Stream disconnected; reconnecting in ${delay / 1000}s.`);
          await sleep(delay);
        }
      }
    };

    void connect();
    return () => { controller.abort(); };
  }, [runId, baseUrl, reconnectKey, maxEvents]);

  return { events, droppedEventCount, status, error, reconnect };
}
