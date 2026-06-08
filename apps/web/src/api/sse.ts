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
