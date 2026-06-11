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

export interface RunSandboxInfo {
  backend: string;
  isRealIsolation: boolean;
}

export interface SandboxPolicy {
  repository_path: string;
  shell_enabled: boolean;
  direct: boolean;
  network_enabled: boolean;
  allowed_repository_roots: string[];
  destructive_command_patterns: string[];
}

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
  sandbox?: RunSandboxInfo | null;
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

export interface WorkspaceFileEntry {
  path: string;
  status: 'added' | 'modified' | 'deleted';
  scope: 'committed' | 'uncommitted' | 'merged';
  added_lines: number;
  removed_lines: number;
}

export interface WorkspaceFileContent {
  path: string;
  content: string | null;
  is_binary: boolean;
  language: string | null;
}

export interface WorkspaceFileDiff {
  path: string;
  diff: string | null;
  status: 'added' | 'modified' | 'deleted';
  is_binary: boolean;
}

export interface WorkspaceNode {
  path: string;
  is_folder: boolean;
  status: 'added' | 'modified' | 'deleted' | null;
}

export interface CommitResponse {
  run_id: string;
  status: string;
}

export interface RequestChangesResponse {
  run_id: string;
  status: string;
}
