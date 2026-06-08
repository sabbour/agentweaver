import { useEffect, useRef, useState } from 'react';
import type { RunDetail } from './types';
import { ScaffolderApiClient } from './client';

export type PollStatus = 'polling' | 'done' | 'error';

interface PollState {
  run: RunDetail | null;
  status: PollStatus;
  error: string | null;
}

const TERMINAL = new Set(['completed', 'failed']);
const POLL_INTERVAL_MS = 2000;

export function useRunPoll(runId: string, apiKey: string, baseUrl: string): PollState {
  const [run, setRun] = useState<RunDetail | null>(null);
  const [status, setStatus] = useState<PollStatus>('polling');
  const [error, setError] = useState<string | null>(null);
  const stopRef = useRef(false);

  useEffect(() => {
    stopRef.current = false;
    const client = new ScaffolderApiClient(baseUrl, apiKey);

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

export type StreamStatus = 'connecting' | 'streaming' | 'done' | 'error';

interface StreamState {
  text: string;
  status: StreamStatus;
  error: string | null;
}

export function useRunStream(runId: string, apiKey: string, baseUrl: string): StreamState {
  const [text, setText] = useState('');
  const [status, setStatus] = useState<StreamStatus>('connecting');
  const [error, setError] = useState<string | null>(null);
  const stopRef = useRef(false);

  useEffect(() => {
    stopRef.current = false;
    setText('');
    setStatus('connecting');
    setError(null);

    const url = `${baseUrl.replace(/\/+$/, '')}/api/runs/${encodeURIComponent(runId)}/stream`;

    const run = async () => {
      try {
        const response = await fetch(url, {
          headers: { Authorization: `Bearer ${apiKey}`, Accept: 'text/event-stream' },
        });
        if (!response.ok) {
          throw new Error(`status ${response.status}`);
        }
        if (!response.body) throw new Error('no body');

        setStatus('streaming');
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (!stopRef.current) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          let sep = buffer.indexOf('\n\n');
          while (sep !== -1) {
            const frame = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);

            let eventType = '';
            let data = '';
            for (const line of frame.split('\n')) {
              if (line.startsWith('event:')) eventType = line.slice(6).trim();
              else if (line.startsWith('data:')) data += line.slice(5);
            }

            if (eventType === 'done') {
              setStatus('done');
              stopRef.current = true;
              break;
            }
            if (data) setText((prev) => prev + data);
            sep = buffer.indexOf('\n\n');
          }
        }
        if (!stopRef.current) setStatus('done');
      } catch (err) {
        if (stopRef.current) return;
        setStatus('error');
        setError(err instanceof Error ? err.message : String(err));
      }
    };

    void run();
    return () => { stopRef.current = true; };
  }, [runId, apiKey, baseUrl]);

  return { text, status, error };
}
