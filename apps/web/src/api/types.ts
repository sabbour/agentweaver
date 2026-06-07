export type ModelSource = 'github-copilot' | 'microsoft-foundry';

export type RunStatus =
  | 'pending'
  | 'in_progress'
  | 'completed'
  | 'failed'
  | 'bounded'
  | 'reviewing'
  | 'approved'
  | 'declined';

export interface SubmitRunRequest {
  repository_path: string;
  originating_branch: string;
  task: string;
  model_source: ModelSource;
}

export interface SubmitRunResponse {
  run_id: string;
  status: RunStatus;
}

export interface RunDetail {
  run_id: string;
  status: RunStatus;
  model_source: ModelSource;
  started_at: string;
  ended_at: string | null;
  step_count: number;
  diff: string | null;
}

// The run event envelope is served by the API stream and the events log.
// The backend serializes the envelope with camelCase member names.
export interface RunEvent {
  runId: string;
  sequence: number;
  type: string;
  timestamp: string;
  payload: Record<string, unknown>;
  callId: string | null;
}

export interface ReviewRequest {
  approved: boolean;
}

export interface ReviewResponse {
  run_id: string;
  status: RunStatus;
  merge_result: string | null;
}
