export type ModelSource = 'github-copilot' | 'microsoft-foundry';

export type RunStatus =
  | 'pending'
  | 'in_progress'
  | 'completed'
  | 'failed'
  | 'awaiting_review'
  | 'merging'
  | 'merged'
  | 'declined'
  | 'merge_failed';

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
  result: string | null;
  diff: string | null;
  step_count: number;
  tree_hash: string | null;
}

export interface ReviewRequest {
  approved: boolean;
}

export interface ReviewResponse {
  run_id: string;
  status: string;
  merge_result: string | null;
}

export interface RetriableReviewErrorBody {
  error: string;
  status: string;
}
