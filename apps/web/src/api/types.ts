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
  worktree_branch?: string | null;
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
  merge_result: string | null;
  conflicting_files: string[] | null;
}

export interface RequestChangesResponse {
  run_id: string;
  status: string;
}

// Projects
export type ProjectOrigin = 'blank' | 'github';
export type ProjectState = 'active' | 'deleting';

export interface Project {
  project_id: string;
  name: string;
  origin: ProjectOrigin;
  source_repository: string | null;
  working_directory: string;
  default_branch: string;
  owner: string;
  default_provider: ModelSource;
  default_model_github_copilot: string | null;
  default_model_microsoft_foundry: string | null;
  available: boolean;
  state: ProjectState;
  created_at: string;
  updated_at: string;
}

export interface CreateProjectRequest {
  name: string;
  origin: ProjectOrigin;
  source_repository?: string;
  working_directory: string;
  default_provider?: ModelSource;
  default_model_github_copilot?: string;
  default_model_microsoft_foundry?: string;
}

export interface UpdateProjectProviderSettingsRequest {
  default_provider?: ModelSource;
  default_model_github_copilot?: string;
  default_model_microsoft_foundry?: string;
}

export interface CreateProjectRunRequest {
  task: string;
  model_source?: ModelSource;
  model_id?: string;
  base_branch?: string;
}

export interface ProjectRunSummary {
  run_id: string;
  status: string;
  model_source: ModelSource;
  model_id: string | null;
  task: string | null;
  started_at: string;
  ended_at: string | null;
}

// GitHub auth
export type GitHubAuthStatus = 'signed_in' | 'signed_out' | 'never_signed_in';

export interface GitHubDeviceFlow {
  user_code: string;
  verification_uri: string;
  expires_in: number;
  interval: number;
}

export interface GitHubPollResult {
  status: 'pending' | 'success' | 'expired' | 'denied';
  login: string | null;
}

export interface GitHubAuthStatusResponse {
  status: GitHubAuthStatus;
  login: string | null;
  avatar_url?: string;
}

export interface GitHubRepo {
  full_name: string;
  description?: string | null;
  private: boolean;
  default_branch: string;
}

// --- Casting / Team types ---

export interface TeamTemplateDto {
  id: string;
  title: string;
  description: string;
  roles: RoleDto[];
}

export interface RoleDto {
  id: string;
  title: string;
  summary: string;
  default_model: string;
}

export interface ProposedMemberDto {
  proposed_name: string;
  role: RoleDto;
  charter_markdown: string;
  is_named: boolean;
  default_model: string;
  justification: string | null;
}

export interface CastProposalDto {
  proposal_id: string;
  mode: 'scenario' | 'free_text' | 'analysis';
  universe: string;
  members: ProposedMemberDto[];
  existing_team_present: boolean;
  run_id: string | null;
  warnings: string[];
}

export interface CreateProposalRequest {
  mode: 'scenario' | 'free_text' | 'analysis';
  template_id?: string;
  goal?: string;
  universe?: string;
  model_id?: string;
}

export interface AmendProposalRequest {
  members?: ProposedMemberDto[];
  universe?: string;
}

export interface ConfirmProposalRequest {
  intent?: 'new' | 'augment' | 'recast';
}

export interface TeamMemberDto {
  name: string;
  role_title: string;
  charter_path: string;
  status: 'active' | 'retired';
  default_model: string;
  is_named: boolean;
}

export interface TeamDto {
  project_name: string;
  universe: string;
  members: TeamMemberDto[];
  layout: 'canonical' | 'legacy' | 'absent';
  migration_available: boolean;
}

export interface CharterDto {
  member_name: string;
  content: string;
}

export interface AddMemberRequest {
  role_id: string;
  custom_role_title?: string;
  model_id?: string;
}

export interface ReroleRequest {
  new_role_id: string;
  custom_role_title?: string;
}

export interface SyncChangeDto {
  path: string;
  kind: 'added' | 'modified' | 'removed';
}

export interface SyncStatusDto {
  changes: SyncChangeDto[];
  change_set_hash: string;
  nothing_to_sync: boolean;
}

export interface SyncCommitRequest {
  expected_change_set_hash: string;
  message?: string;
}

export interface SyncCommitResponseDto {
  commit_id: string;
}
