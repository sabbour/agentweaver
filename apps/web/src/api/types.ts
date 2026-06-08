export type ModelSource = 'github-copilot' | 'microsoft-foundry';

export type RunStatus = 'pending' | 'in_progress' | 'completed' | 'failed';

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
}
