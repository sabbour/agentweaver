/**
 * T060: Typed TypeScript API client for the Scaffolder API.
 * Generated from contracts/run-api.yaml targeting /api (proxied by Vite to http://localhost:3000).
 */

export interface CreateRunRequest {
  originatingBranch: string;
  modelSource: 'CopilotSdk' | 'MicrosoftFoundry';
  taskPrompt: string;
  maxSteps?: number;
  maxDurationSeconds?: number;
}

export interface RunResponse {
  id: string;
  originatingBranch: string;
  modelSource: string;
  taskPrompt: string;
  submittedBy: string;
  status: RunStatus;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  maxSteps: number;
  maxDurationSeconds: number;
  sessionId?: string;
  diffSummary?: string;
  failureReason?: string;
}

export type RunStatus =
  | 'Queued'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'Bounded'
  | 'AwaitingReview'
  | 'Approved'
  | 'Declined'
  | 'Merged'
  | 'MergeConflict';

export interface ReviewDecisionRequest {
  decision: 'approve' | 'decline';
  reviewer: string;
  comment?: string;
}

export interface SseEvent {
  sequence: number;
  eventType: string;
  data: string;
}

const BASE_URL = '/api';

async function apiFetch<T>(
  path: string,
  options?: RequestInit,
): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    ...options,
  });

  if (!response.ok) {
    const body = await response.text();
    throw new ApiError(response.status, body);
  }

  return response.json() as Promise<T>;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: string,
  ) {
    super(`API error ${status}: ${body}`);
  }
}

// POST /runs
export function createRun(
  request: CreateRunRequest,
  submittedBy?: string,
): Promise<RunResponse> {
  return apiFetch<RunResponse>('/runs', {
    method: 'POST',
    body: JSON.stringify(request),
    headers: submittedBy ? { 'X-Submitted-By': submittedBy } : {},
  });
}

// GET /runs/{runId}
export function getRun(runId: string): Promise<RunResponse> {
  return apiFetch<RunResponse>(`/runs/${runId}`);
}

// GET /runs/{runId}/diff
export async function getRunDiff(runId: string): Promise<string> {
  const response = await fetch(`${BASE_URL}/runs/${runId}/diff`);
  if (!response.ok) {
    const body = await response.text();
    throw new ApiError(response.status, body);
  }
  return response.text();
}

// POST /runs/{runId}/review
export function reviewRun(
  runId: string,
  request: ReviewDecisionRequest,
): Promise<RunResponse> {
  return apiFetch<RunResponse>(`/runs/${runId}/review`, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

// GET /runs/{runId}/stream (SSE)
export function createRunStream(
  runId: string,
  lastEventId?: number,
): EventSource {
  const url = lastEventId !== undefined
    ? `${BASE_URL}/runs/${runId}/stream?lastSeenSequence=${lastEventId}`
    : `${BASE_URL}/runs/${runId}/stream`;
  return new EventSource(url);
}

export const TERMINAL_EVENT_TYPES = new Set([
  'run.failed',
  'run.bounded',
  'review.declined',
  'merge.completed',
  'merge.failed',
]);
