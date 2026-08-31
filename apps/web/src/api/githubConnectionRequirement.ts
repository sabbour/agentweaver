export const GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT = 'agentweaver:github-copilot-connection-required';
export const GITHUB_COPILOT_CONNECTION_REQUIRED_CODE = 'github_copilot_connection_required';
export const GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE =
  'Connect your GitHub Copilot account to continue.';
export const CONNECT_PROJECT_COPILOT_APP_ACTION = 'connect_project_copilot_app';

export interface GitHubCopilotConnectionRequirement {
  code: string;
  message: string;
  action: {
    type: typeof CONNECT_PROJECT_COPILOT_APP_ACTION;
    project_id: string;
  };
}

export function isGitHubCopilotConnectionRequirement(value: unknown): value is GitHubCopilotConnectionRequirement {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  const action = record.action;
  return typeof record.code === 'string'
    && typeof record.message === 'string'
    && typeof action === 'object'
    && action !== null
    && (action as Record<string, unknown>).type === CONNECT_PROJECT_COPILOT_APP_ACTION
    && typeof (action as Record<string, unknown>).project_id === 'string';
}
